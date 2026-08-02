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
    private readonly TrayApplicationContext _tray;
    private readonly UiDispatcher _ui;
    private readonly ILogger<AppHost> _log;

    private bool _disposed;

    public AppHost(
        SettingsService settings,
        MusicLibrary library,
        AudioEngineThread engine,
        PlaybackEngine playback,
        TrayApplicationContext tray,
        UiDispatcher ui,
        ILogger<AppHost> log)
    {
        _settings = settings;
        _library = library;
        _engine = engine;
        _playback = playback;
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

        _playback.Configure(settings);

        _tray.ExitRequested += (_, _) => _log.LogInformation("Exit requested from the tray.");
        _tray.NextRequested += (_, _) => _playback.Next();
        _tray.PreviousRequested += (_, _) => _playback.Previous();
        _tray.ReopenDeviceRequested += (_, _) => _playback.ReopenDevice();

        _log.LogInformation("NoSilence started.");
    }

    private void OnPlaybackStateChanged(object? sender, PlaybackSnapshot snapshot)
    {
        _ui.Post(() => _tray.Apply(snapshot));
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
        _engine.Tick -= _playback.Poll;

        // Release the device before stopping the thread that owns it.
        _playback.Dispose();
        _engine.Dispose();
        _library.Dispose();
        _settings.Save();
    }
}
