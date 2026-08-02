using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NoSilence.Audio;
using NoSilence.Settings;

namespace NoSilence.Playback;

/// <summary>
/// Owns the sample graph and the output device.
/// </summary>
/// <remarks>
/// The graph — playlist, user volume, ducking — is built once and outlives every device
/// change. Only the <see cref="WasapiOut"/> sink is torn down and rebuilt, which is what
/// lets the music resume mid-track when the TV comes back on.
/// <para>
/// Everything here runs on <see cref="AudioEngineThread"/>. Public methods are safe to call
/// from anywhere; they post inward.
/// </para>
/// </remarks>
internal sealed class PlaybackEngine : IDisposable
{
    private readonly AudioEngineThread _engine;
    private readonly OutputDeviceResolver _resolver;
    private readonly MusicLibrary _library;
    private readonly ShuffleQueue _queue;
    private readonly PlaylistSampleProvider _playlist;
    private readonly VolumeSampleProvider _volume;
    private readonly DuckingSampleProvider _ducking;
    private readonly ILogger<PlaybackEngine> _log;

    private WasapiOut? _output;
    private MMDevice? _device;
    private OutputSettings _settings = new();
    private PlaybackPhase _phase = PlaybackPhase.Idle;
    private string? _detail;
    private string? _deviceName;
    private string? _lastLoggedTrackPath;
    private bool _disposed;

    public PlaybackEngine(
        AudioEngineThread engine,
        OutputDeviceResolver resolver,
        MusicLibrary library,
        ShuffleQueue queue,
        PlaylistSampleProvider playlist,
        ILogger<PlaybackEngine> log)
    {
        _engine = engine;
        _resolver = resolver;
        _library = library;
        _queue = queue;
        _playlist = playlist;
        _log = log;

        _volume = new VolumeSampleProvider(_playlist) { Volume = 0.2f };
        _ducking = new DuckingSampleProvider(_volume);

        _playlist.TrackChanged += OnTrackChanged;
        _playlist.Stalled += (_, message) => _engine.Post(() => SetPhase(PlaybackPhase.Faulted, message));
        _library.Changed += (_, _) => _engine.Post(RebuildQueue);
    }

    /// <summary>Raised whenever the published state changes. Fires on the engine thread.</summary>
    public event EventHandler<PlaybackSnapshot>? StateChanged;

    public PlaybackSnapshot Snapshot => new(
        _phase,
        _playlist.CurrentTrack,
        _playlist.Position,
        _playlist.Duration,
        _ducking.CurrentGain * _volume.Volume,
        _deviceName,
        _detail);

    /// <summary>Applies settings and (re)opens the device if the target changed.</summary>
    public void Configure(AppSettings settings) => _engine.Post(() =>
    {
        bool deviceChanged =
            !string.Equals(_settings.DeviceId, settings.Output.DeviceId, StringComparison.Ordinal) ||
            !string.Equals(_settings.DeviceFriendlyName, settings.Output.DeviceFriendlyName, StringComparison.Ordinal) ||
            _settings.LatencyMs != settings.Output.LatencyMs;

        _settings = settings.Output;
        _queue.Shuffle = settings.Library.Shuffle;
        _queue.NoRepeatWindow = settings.Library.NoRepeatWindow;
        _volume.Volume = Math.Clamp(settings.Output.VolumePercent / 100f, 0f, 1f);

        RebuildQueue();

        if (deviceChanged || _output is null)
        {
            OpenDevice();
        }
    });

    /// <summary>Sets the ducking gain. 1 is full volume, 0 is silence.</summary>
    public void SetGain(float gain, int fadeMs) => _ducking.SetTarget(gain, fadeMs);

    public void Next() => _playlist.Next();

    public void Previous() => _playlist.Previous();

    /// <summary>Re-resolves and reopens the output device. Used by the tray and after a fault.</summary>
    public void ReopenDevice() => _engine.Post(OpenDevice);

    /// <summary>Publishes a fresh snapshot; called from the engine tick.</summary>
    public void Poll()
    {
        if (_phase is PlaybackPhase.Playing or PlaybackPhase.Ducked or PlaybackPhase.Silenced)
        {
            PlaybackPhase expected = _ducking.TargetGain <= 0.001f
                ? (_phase == PlaybackPhase.Silenced ? PlaybackPhase.Silenced : PlaybackPhase.Ducked)
                : PlaybackPhase.Playing;

            if (expected != _phase)
            {
                SetPhase(expected, null);
                return;
            }
        }

        StateChanged?.Invoke(this, Snapshot);
    }

    /// <summary>
    /// Fires twice per track — once when the file opens and again if tags arrive later —
    /// so the log entry is keyed on the path rather than the announcement.
    /// </summary>
    private void OnTrackChanged(object? sender, TrackInfo track)
    {
        if (string.Equals(_lastLoggedTrackPath, track.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _lastLoggedTrackPath = track.Path;
        _log.LogInformation("Now playing: {Track}", track.DisplayName);
    }

    private void RebuildQueue()
    {
        _queue.Rebuild(_library.Tracks);
        _playlist.OnLibraryChanged();

        if (_library.Tracks.Count == 0 && _phase != PlaybackPhase.NoDevice)
        {
            SetPhase(PlaybackPhase.Idle, "No music files found. Add a folder in Settings.");
        }
    }

    private void OpenDevice()
    {
        CloseDevice();

        if (_library.Tracks.Count == 0)
        {
            SetPhase(PlaybackPhase.Idle, "No music files found. Add a folder in Settings.");
            return;
        }

        DeviceResolutionResult resolution = _resolver.Resolve(_settings);
        if (!resolution.Success)
        {
            _deviceName = null;
            PlaybackPhase phase = resolution.Outcome switch
            {
                DeviceResolution.NotConfigured => PlaybackPhase.Idle,
                DeviceResolution.PresentButInactive => PlaybackPhase.NoDevice,
                _ => PlaybackPhase.NoDevice,
            };

            SetPhase(phase, resolution.Description);
            return;
        }

        _device = resolution.Device;
        _deviceName = resolution.Description;
        SetPhase(PlaybackPhase.Opening, null);

        try
        {
            // Shared mode, always. Exclusive mode would lock every other application out of
            // the endpoint, which is the opposite of what a background music player should do.
            var output = new WasapiOut(_device!, AudioClientShareMode.Shared, useEventSync: true, Math.Clamp(_settings.LatencyMs, 50, 1000));
            output.PlaybackStopped += OnPlaybackStopped;

            _ducking.Reset(_ducking.TargetGain);
            output.Init(_ducking);
            output.Play();

            _output = output;
            SetPhase(_ducking.TargetGain > 0.001f ? PlaybackPhase.Playing : PlaybackPhase.Ducked, null);
            _log.LogInformation("Playing to {Device} at {Latency} ms.", _deviceName, _settings.LatencyMs);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            _log.LogError(ex, "Could not open the output device {Device}.", _deviceName);
            CloseDevice();
            SetPhase(PlaybackPhase.NoDevice, $"Could not open {_deviceName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Fires on NAudio's playback thread. Disposing the output from inside this handler
    /// deadlocks, so the teardown is always posted to the engine thread instead.
    /// </summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null || _disposed)
        {
            return;
        }

        _log.LogWarning(e.Exception, "Playback stopped unexpectedly; the device was probably removed.");
        _engine.Post(() =>
        {
            CloseDevice();
            SetPhase(PlaybackPhase.NoDevice, "The output device disappeared. Waiting for it to come back.");
        });
    }

    private void CloseDevice()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;

            try
            {
                _output.Stop();
                _output.Dispose();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
            {
                // The endpoint is already gone; there is nothing left to clean up on it.
                _log.LogDebug(ex, "Ignoring an error while closing the output device.");
            }

            _output = null;
        }

        _device?.Dispose();
        _device = null;
    }

    private void SetPhase(PlaybackPhase phase, string? detail)
    {
        if (_phase == phase && string.Equals(_detail, detail, StringComparison.Ordinal))
        {
            return;
        }

        _phase = phase;
        _detail = detail;
        _log.LogDebug("Playback phase: {Phase} {Detail}", phase, detail);
        StateChanged?.Invoke(this, Snapshot);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.Invoke(() =>
        {
            CloseDevice();
            _playlist.Dispose();
        });
    }
}
