using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
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
/// The device state machine is the important part of this class, because the target output
/// is an HDMI endpoint that Windows deletes outright whenever the TV powers off or switches
/// input:
/// </para>
/// <code>
/// Idle ──(configured)──► Settling ──(timer)──► [resolve]
///                                                 │
///          ┌──────────── missing ─────────────────┤
///          ▼                                      ▼ present
///       Waiting ──(5 s, or a notification)──► [open]
///                                              │      │
///                                    ok ◄──────┘      └──────► Backoff (1 s → 30 s)
///                                    ▼                              │
///                                 Running ◄─────────────────────────┘
///                                    │
///          removed / stopped / COM ──┴──► Settling
/// </code>
/// <para>Everything here runs on <see cref="AudioEngineThread"/>; public methods post inward.</para>
/// </remarks>
internal sealed class PlaybackEngine : IDisposable
{
    private enum OutputState
    {
        /// <summary>Nothing to do — no library, or no device configured.</summary>
        Idle,

        /// <summary>Waiting out a settle delay before trying the device.</summary>
        Settling,

        /// <summary>The device is not present. Re-checking at a steady interval.</summary>
        Waiting,

        /// <summary>The device is present but would not open. Backing off.</summary>
        Backoff,

        /// <summary>Audio is flowing.</summary>
        Running,
    }

    private readonly AudioEngineThread _engine;
    private readonly DeviceCatalog _catalog;
    private readonly OutputDeviceResolver _resolver;
    private readonly SettingsService _settingsService;
    private readonly MusicLibrary _library;
    private readonly ShuffleQueue _queue;
    private readonly PlaylistSampleProvider _playlist;
    private readonly VolumeSampleProvider _volume;
    private readonly DuckingSampleProvider _ducking;
    private readonly DeviceRetryPolicy _retry = new();
    private readonly ILogger<PlaybackEngine> _log;

    private EndpointNotificationBridge? _notifications;
    private WasapiOut? _output;
    private MMDevice? _device;
    private string? _activeDeviceId;
    private OutputSettings _settings = new();
    private OutputState _outputState = OutputState.Idle;
    private long _nextAttemptAt;
    private PlaybackPhase _phase = PlaybackPhase.Idle;
    private string? _detail;
    private string? _deviceName;
    private string? _lastLoggedTrackPath;
    private string? _outputWarning;
    private string? _decisionReason;
    private bool _decisionIsOverride;
    private long _nextVolumeCheckAt;
    private bool _disposed;

    public PlaybackEngine(
        AudioEngineThread engine,
        DeviceCatalog catalog,
        OutputDeviceResolver resolver,
        SettingsService settingsService,
        MusicLibrary library,
        ShuffleQueue queue,
        PlaylistSampleProvider playlist,
        ILogger<PlaybackEngine> log)
    {
        _engine = engine;
        _catalog = catalog;
        _resolver = resolver;
        _settingsService = settingsService;
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

    /// <summary>
    /// Raised when a device we were playing on disappeared unexpectedly. M8 uses this as the
    /// signal that the user switched the TV off by hand, so it can stop trying to wake it.
    /// </summary>
    public event EventHandler? OutputDeviceLost;

    /// <summary>Raised when the output device opens successfully.</summary>
    public event EventHandler? OutputDeviceAcquired;

    public PlaybackSnapshot Snapshot => new(
        _phase,
        _playlist.CurrentTrack,
        _playlist.Position,
        _playlist.Duration,
        _ducking.CurrentGain * _volume.Volume,
        _deviceName,
        _detail)
    {
        Warning = _outputWarning,
    };

    /// <summary>True while audio is flowing. Used as the "is the TV on" sensor from M8.</summary>
    public bool HasOutputDevice => _outputState == OutputState.Running;

    public void Start()
    {
        _notifications = new EndpointNotificationBridge(
            notification => _engine.Post(() => OnEndpointEvent(notification)),
            _log);

        _catalog.RegisterNotifications(_notifications);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

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

        if (deviceChanged || _outputState is OutputState.Idle)
        {
            ScheduleReopen(delayMs: 0, "settings changed");
        }
    });

    /// <summary>Sets the ducking gain. 1 is full volume, 0 is silence.</summary>
    public void SetGain(float gain, int fadeMs) => _ducking.SetTarget(gain, fadeMs);

    /// <summary>
    /// Applies a decision from the detection engine: the gain, the fade, and the
    /// human-readable reason the tray shows.
    /// </summary>
    public void ApplyDecision(Detection.DecisionOutcome outcome)
    {
        _ducking.SetTarget(outcome.TargetGain, outcome.FadeMs);
        _decisionReason = outcome.WantsSilence ? outcome.Reason : null;
        _decisionIsOverride = outcome.Phase == Detection.DecisionPhase.Overridden;
    }

    public void Next() => _playlist.Next();

    public void Previous() => _playlist.Previous();

    /// <summary>Forces an immediate reconnect attempt. The tray's "Reconnect output device".</summary>
    public void ReopenDevice() => _engine.Post(() =>
    {
        _retry.Reset();
        ScheduleReopen(delayMs: 0, "requested by the user");
    });

    /// <summary>Drives the state machine and publishes a snapshot. Called from the engine tick.</summary>
    public void Poll()
    {
        if (_disposed)
        {
            return;
        }

        if (_outputState is OutputState.Settling or OutputState.Waiting or OutputState.Backoff
            && Environment.TickCount64 >= _nextAttemptAt)
        {
            TryOpenDevice();
        }

        CheckOutputIsAudible();

        if (_phase is PlaybackPhase.Playing or PlaybackPhase.Ducked or PlaybackPhase.Silenced)
        {
            PlaybackPhase expected = _ducking.TargetGain <= 0.001f
                ? (_decisionIsOverride ? PlaybackPhase.Silenced : PlaybackPhase.Ducked)
                : PlaybackPhase.Playing;

            SetPhase(expected, expected == PlaybackPhase.Playing ? null : _decisionReason);
        }

        StateChanged?.Invoke(this, Snapshot);
    }

    /// <summary>
    /// Notices when we are playing into an endpoint nobody could hear.
    /// </summary>
    /// <remarks>
    /// The TV's endpoint carries its own Windows volume slider, independent of the one you
    /// normally touch, and it is commonly left at zero — nothing else routes audio there, so
    /// nothing else has ever revealed it. Without this check the app reports "Playing", the
    /// log looks perfect, and the room is silent, which is impossible to debug from the
    /// outside. Session volume is checked too, in case the Volume Mixer has a stale entry
    /// for NoSilence.
    /// </remarks>
    private void CheckOutputIsAudible()
    {
        if (_outputState != OutputState.Running || _device is null)
        {
            if (_outputWarning is not null)
            {
                _outputWarning = null;
            }

            return;
        }

        long now = Environment.TickCount64;
        if (now < _nextVolumeCheckAt)
        {
            return;
        }

        _nextVolumeCheckAt = now + 1000;

        string? warning = null;
        try
        {
            AudioEndpointVolume endpoint = _device.AudioEndpointVolume;
            if (endpoint.Mute)
            {
                warning = $"{_deviceName} is muted in Windows, so nothing will be heard.";
            }
            else if (endpoint.MasterVolumeLevelScalar < 0.02f)
            {
                warning = $"{_deviceName} is turned down to {endpoint.MasterVolumeLevelScalar * 100:F0}% in Windows, so nothing will be heard.";
            }
        }
        catch (COMException ex)
        {
            // The endpoint is going away; the state machine will pick that up shortly.
            _log.LogDebug(ex, "Could not read the output endpoint volume.");
            return;
        }

        if (string.Equals(_outputWarning, warning, StringComparison.Ordinal))
        {
            return;
        }

        if (warning is not null)
        {
            _log.LogWarning("{Warning}", warning);
        }
        else if (_outputWarning is not null)
        {
            _log.LogInformation("{Device} is audible again.", _deviceName);
        }

        _outputWarning = warning;
        StateChanged?.Invoke(this, Snapshot);
    }

    // ---- device state machine -------------------------------------------

    /// <summary>
    /// Reacts to a WASAPI endpoint notification. Already marshalled onto the engine thread.
    /// </summary>
    private void OnEndpointEvent(EndpointEvent notification)
    {
        bool concernsActiveDevice = _activeDeviceId is not null
            && string.Equals(notification.DeviceId, _activeDeviceId, StringComparison.OrdinalIgnoreCase);

        // Our device went away while we were using it.
        if (_outputState == OutputState.Running && concernsActiveDevice && IsLoss(notification))
        {
            _log.LogInformation("Output device disappeared ({Notification}).", notification);
            CloseDevice();
            OutputDeviceLost?.Invoke(this, EventArgs.Empty);
            SetPhase(PlaybackPhase.NoDevice, $"{_deviceName} disconnected. Waiting for it to come back.");
            ScheduleReopen(_retry.SettleMs, "device removed");
            return;
        }

        if (_outputState == OutputState.Running)
        {
            return;   // already playing and this is about some other endpoint
        }

        // Something appeared or changed state while we were waiting. We cannot cheaply tell
        // whether it is *our* device — resolution may be by name, and the ID can change
        // across a driver reinstall — so any arrival is worth one attempt. A settle delay
        // keeps the burst of notifications Windows fires down to a single try.
        if (notification.Kind is EndpointEventKind.Added or EndpointEventKind.StateChanged or EndpointEventKind.DefaultChanged)
        {
            _log.LogDebug("Endpoint notification while waiting: {Notification}.", notification);
            _retry.Reset();
            ScheduleReopen(_retry.SettleMs, "endpoint notification");
        }
    }

    private static bool IsLoss(EndpointEvent notification) =>
        notification.Kind == EndpointEventKind.Removed ||
        (notification.Kind == EndpointEventKind.StateChanged && notification.NewState != DeviceState.Active);

    private void ScheduleReopen(int delayMs, string reason)
    {
        CloseDevice();

        if (_library.Tracks.Count == 0)
        {
            _outputState = OutputState.Idle;
            SetPhase(PlaybackPhase.Idle, "No music files found. Add a folder in Settings.");
            return;
        }

        _outputState = OutputState.Settling;
        _nextAttemptAt = Environment.TickCount64 + Math.Max(0, delayMs);
        _log.LogDebug("Will try the output device in {Delay} ms ({Reason}).", delayMs, reason);
    }

    private void TryOpenDevice()
    {
        if (_library.Tracks.Count == 0)
        {
            _outputState = OutputState.Idle;
            SetPhase(PlaybackPhase.Idle, "No music files found. Add a folder in Settings.");
            return;
        }

        DeviceResolutionResult resolution = _resolver.Resolve(_settings);

        if (!resolution.Success)
        {
            _deviceName = null;
            _outputState = resolution.Outcome == DeviceResolution.NotConfigured
                ? OutputState.Idle
                : OutputState.Waiting;

            int retryIn = _retry.NextDelayAfterMissingDevice();
            _nextAttemptAt = Environment.TickCount64 + retryIn;

            // SetPhase deduplicates, so without this the log goes silent while waiting and
            // there is no way to tell a working retry loop from a stuck one.
            _log.LogDebug("Output device not present ({Reason}); checking again in {Delay} ms.", resolution.Description, retryIn);

            SetPhase(
                resolution.Outcome == DeviceResolution.NotConfigured ? PlaybackPhase.Idle : PlaybackPhase.NoDevice,
                resolution.Description);
            return;
        }

        _device = resolution.Device;
        _deviceName = resolution.Description;
        RememberResolvedId(_device!);

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
            _activeDeviceId = _device!.ID;
            _outputState = OutputState.Running;
            _retry.Reset();

            SetPhase(_ducking.TargetGain > 0.001f ? PlaybackPhase.Playing : PlaybackPhase.Ducked, null);
            _log.LogInformation("Playing to {Device} at {Latency} ms.", _deviceName, _settings.LatencyMs);
            OutputDeviceAcquired?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            CloseDevice();

            int delay = _retry.NextDelayAfterOpenFailure();
            _outputState = OutputState.Backoff;
            _nextAttemptAt = Environment.TickCount64 + delay;

            // A device that is present but not yet initialisable is normal for a few seconds
            // after an HDMI sink appears, so the first couple of failures are not warnings.
            if (_retry.ConsecutiveFailures <= 2)
            {
                _log.LogDebug(ex, "Output device not ready yet; retrying in {Delay} ms.", delay);
            }
            else
            {
                _log.LogWarning(
                    "Could not open {Device} ({Failures} attempts): {Message}. Retrying in {Delay} ms.",
                    _deviceName,
                    _retry.ConsecutiveFailures,
                    ex.Message,
                    delay);
            }

            SetPhase(PlaybackPhase.NoDevice, $"Could not open {_deviceName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes back an endpoint ID that changed identity — a GPU driver reinstall can mint a
    /// new one for the same physical output, and we would otherwise fall back to the fuzzy
    /// name match on every single launch.
    /// </summary>
    private void RememberResolvedId(MMDevice device)
    {
        string id;
        try
        {
            id = device.ID;
        }
        catch (COMException)
        {
            return;
        }

        if (string.Equals(_settings.DeviceId, id, StringComparison.Ordinal))
        {
            return;
        }

        _log.LogInformation("Recording the output device's endpoint ID as {Id}.", id);
        _settings.DeviceId = id;
        _settingsService.Current.Output.DeviceId = id;
        _settingsService.Save();
    }

    /// <summary>
    /// Fires on NAudio's playback thread. Disposing the output from inside this handler
    /// deadlocks, so teardown is always posted to the engine thread instead.
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
            if (_outputState != OutputState.Running)
            {
                return;   // we already noticed via a notification
            }

            CloseDevice();
            OutputDeviceLost?.Invoke(this, EventArgs.Empty);
            SetPhase(PlaybackPhase.NoDevice, "The output device disappeared. Waiting for it to come back.");
            ScheduleReopen(_retry.SettleMs, "playback stopped");
        });
    }

    /// <summary>
    /// Endpoint IDs can change identity across sleep on some GPU drivers, and the whole
    /// audio stack re-enumerates on resume, so the device is rebuilt from scratch with a
    /// longer settle rather than trusted to have survived.
    /// </summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _engine.Post(() =>
        {
            _log.LogInformation("Resumed from sleep; rebuilding the output device.");
            _retry.Reset();
            ScheduleReopen(_retry.ResumeSettleMs, "resumed from sleep");
        });
    }

    // ---- playlist / library ---------------------------------------------

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

        if (_library.Tracks.Count == 0)
        {
            if (_outputState != OutputState.Idle)
            {
                CloseDevice();
                _outputState = OutputState.Idle;
            }

            SetPhase(PlaybackPhase.Idle, "No music files found. Add a folder in Settings.");
        }
        else if (_outputState == OutputState.Idle)
        {
            // Files appeared in a previously empty library.
            ScheduleReopen(delayMs: 0, "library is no longer empty");
        }
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
        _activeDeviceId = null;
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

        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _catalog.UnregisterNotifications();
        _notifications = null;

        _engine.Invoke(() =>
        {
            CloseDevice();
            _playlist.Dispose();
        });
    }
}
