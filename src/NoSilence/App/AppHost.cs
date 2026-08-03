using Microsoft.Extensions.Logging;
using NoSilence.Audio;
using NoSilence.Playback;
using NoSilence.Settings;
using NoSilence.Ui;

namespace NoSilence.App;

/// <summary>
/// Startup and shutdown order for the running app.
/// </summary>
/// <remarks>
/// Ordering is the whole point of this class: settings before the library, the library
/// before the engine thread, the engine thread before the device, and the exact reverse on
/// the way out — with the device released before the thread that owns it stops.
/// </remarks>
internal sealed class AppHost : IDisposable
{
    private readonly SettingsService _settings;
    private readonly MusicLibrary _library;
    private readonly AudioEngineThread _engine;
    private readonly PlaybackEngine _playback;
    private readonly Detection.DetectionService _detection;
    private readonly TrayApplicationContext _tray;
    private readonly UiDispatcher _ui;
    private readonly AppController _controller;
    private readonly StartupRegistration _startup;
    private readonly Tv.TvService _tv;
    private readonly StateService _state;
    private readonly ILogger<AppHost> _log;

    private SettingsForm? _settingsForm;
    private bool _disposed;

    public AppHost(
        SettingsService settings,
        MusicLibrary library,
        AudioEngineThread engine,
        PlaybackEngine playback,
        Detection.DetectionService detection,
        TrayApplicationContext tray,
        UiDispatcher ui,
        AppController controller,
        StartupRegistration startup,
        Tv.TvService tv,
        StateService state,
        ILogger<AppHost> log)
    {
        _controller = controller;
        _startup = startup;
        _tv = tv;
        _state = state;
        _settings = settings;
        _library = library;
        _engine = engine;
        _playback = playback;
        _detection = detection;
        _tray = tray;
        _ui = ui;
        _log = log;
    }

    public void Start(bool resetSettings)
    {
        AppSettings settings = _settings.Load(resetSettings);

        if (_settings.LoadWarning is { } warning)
        {
            _log.LogWarning("{Warning}", warning);
        }

        _library.Configure(settings.Library);

        _playback.StateChanged += OnPlaybackStateChanged;
        _engine.Tick += _playback.Poll;
        _engine.Start();

        // Registered after the engine thread exists, because endpoint notifications are
        // posted straight onto it.
        _playback.Start();
        _playback.Configure(settings);

        _detection.Configure(settings.Detection);
        _detection.Decided += OnDecided;
        _engine.Tick += _detection.Tick;

        // An endpoint appearing or disappearing changes which sessions exist, so the cached
        // session list has to be rebuilt rather than waiting out the refresh interval.
        _playback.OutputDeviceAcquired += (_, _) => _detection.InvalidateSessions();
        _playback.OutputDeviceLost += (_, _) =>
        {
            _detection.InvalidateSessions();
            _tv.NoteOutputDeviceLost();
        };

        _state.Load();
        _tv.Configure(
            endpointPresent: () => _playback.HasOutputDevice,
            wantsToPlay: () => _detection.LastOutcome is { WantsSilence: false },
            libraryHasTracks: () => _library.Tracks.Count > 0,
            currentOverride: () => _detection.Override);

        _engine.Tick += _tv.Tick;

        _tray.ExitRequested += (_, _) => _log.LogInformation("Exit requested from the tray.");
        _tray.SettingsRequested += (_, e) =>
            ShowSettings(e is TrayApplicationContext.ShowLiveViewArgs ? SettingsForm.LiveTabTitle : null);

        _startup.RepairIfStale();

        _log.LogInformation("NoSilence started.");
    }

    /// <summary>
    /// Shows the settings window, creating it on first use. It is kept alive and hidden
    /// afterwards, so reopening is instant.
    /// </summary>
    public void ShowSettings(string? tab = null)
    {
        _settingsForm ??= new SettingsForm(_controller, _startup);

        _settingsForm.ReloadFromSettings();

        if (tab is not null)
        {
            _settingsForm.SelectTab(tab);
        }

        _settingsForm.Show();

        if (_settingsForm.WindowState == System.Windows.Forms.FormWindowState.Minimized)
        {
            _settingsForm.WindowState = System.Windows.Forms.FormWindowState.Normal;
        }

        _settingsForm.BringToFront();
        _settingsForm.Activate();
    }

    private void OnPlaybackStateChanged(object? sender, PlaybackSnapshot snapshot)
    {
        _ui.Post(() => _tray.Apply(snapshot));
    }

    /// <summary>
    /// The whole point of the app, in one line: what the detection engine decided becomes
    /// the gain on the music.
    /// </summary>
    private void OnDecided(object? sender, (Detection.DecisionOutcome Outcome, Detection.DetectionSnapshot Snapshot) e)
    {
        _playback.ApplyDecision(e.Outcome);

        // Only marshal to the UI when someone is actually looking. At 4 Hz this would
        // otherwise post a message to the UI thread forever for a window nobody opened.
        if (_settingsForm is { Visible: true, Live: not null } form)
        {
            Detection.DecisionOutcome outcome = e.Outcome;
            _ui.Post(() =>
            {
                if (form is { IsDisposed: false, Visible: true, Live: not null })
                {
                    form.Live.Update(outcome);
                }
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _log.LogInformation("Shutting down.");

        _playback.StateChanged -= OnPlaybackStateChanged;
        _detection.Decided -= OnDecided;
        _engine.Tick -= _playback.Poll;
        _engine.Tick -= _detection.Tick;
        _engine.Tick -= _tv.Tick;
        _tv.Dispose();

        _settingsForm?.Dispose();

        // Release the device before stopping the thread that owns it.
        _playback.Dispose();
        _detection.Dispose();
        _engine.Dispose();
        _library.Dispose();
        _settings.Save();
    }
}
