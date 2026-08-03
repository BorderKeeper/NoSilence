using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NoSilence.Audio;
using NoSilence.Detection;
using NoSilence.Playback;
using NoSilence.Settings;

namespace NoSilence.App;

/// <summary>
/// Everything the user interface is allowed to ask the app to do.
/// </summary>
/// <remarks>
/// A single facade rather than a dozen events on the tray context. The tray is then a view:
/// it renders state and calls methods, and none of the settings-saving or engine-poking
/// logic lives in a click handler.
/// </remarks>
internal sealed class AppController
{
    private readonly SettingsService _settings;
    private readonly PlaybackEngine _playback;
    private readonly DetectionService _detection;
    private readonly DeviceCatalog _catalog;
    private readonly MusicLibrary _library;
    private readonly AppPaths _paths;
    private readonly Tv.TvService _tv;
    private readonly Tv.Samsung.SamsungDiscovery _discovery;
    private readonly ILogger<AppController> _log;

    public AppController(
        SettingsService settings,
        PlaybackEngine playback,
        DetectionService detection,
        DeviceCatalog catalog,
        MusicLibrary library,
        AppPaths paths,
        Tv.TvService tv,
        Tv.Samsung.SamsungDiscovery discovery,
        ILogger<AppController> log)
    {
        _tv = tv;
        _discovery = discovery;
        _settings = settings;
        _playback = playback;
        _detection = detection;
        _catalog = catalog;
        _library = library;
        _paths = paths;
        _log = log;
    }

    public AppSettings Settings => _settings.Current;

    public PlaybackSnapshot Playback => _playback.Snapshot;

    public DecisionOutcome? LastDecision => _detection.LastOutcome;

    public OverrideState Override => _detection.Override;

    public int TrackCount => _library.Tracks.Count;

    // ---- transport -------------------------------------------------------

    public void NextTrack() => _playback.Next();

    public void PreviousTrack() => _playback.Previous();

    public void SetVolume(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        _playback.SetVolume(percent);
        _settings.Current.Output.VolumePercent = percent;
        _settings.Save();
    }

    // ---- mode and snooze -------------------------------------------------

    public void SetMode(OperatingMode mode) =>
        _detection.Override = new OverrideState(mode, SnoozeUntil: null);

    /// <summary>Go quiet for a while, then return to whatever the mode was.</summary>
    public void Snooze(TimeSpan duration) =>
        _detection.Override = _detection.Override with { SnoozeUntil = DateTimeOffset.Now + duration };

    /// <summary>Stay quiet until explicitly turned back on.</summary>
    public void SnoozeIndefinitely() => SetMode(OperatingMode.AlwaysSilent);

    public void CancelSnooze() =>
        _detection.Override = _detection.Override with { SnoozeUntil = null };

    // ---- output device ---------------------------------------------------

    /// <summary>
    /// Render endpoints for the menu, including ones that are not currently connected.
    /// </summary>
    /// <remarks>
    /// Absent devices are listed on purpose: the TV is switched off most of the time, and a
    /// picker that hides it would be unusable exactly when you want to configure it.
    /// </remarks>
    public IReadOnlyList<AudioEndpointInfo> ListOutputDevices() => _catalog.List(
        DataFlow.Render,
        DeviceState.Active | DeviceState.Unplugged | DeviceState.NotPresent);

    public void SelectOutputDevice(AudioEndpointInfo device)
    {
        _log.LogInformation("Output device set to {Device}.", device.FriendlyName);

        _settings.Current.Output.DeviceId = device.Id;
        _settings.Current.Output.DeviceFriendlyName = device.FriendlyName;
        _settings.Save();

        _playback.Configure(_settings.Current);
    }

    public void ReopenDevice() => _playback.ReopenDevice();

    public void PlayTestTone(string endpointId) =>
        _playback.PlayTestTone(endpointId, _settings.Current.Output.VolumePercent);

    // ---- library ---------------------------------------------------------

    public void SetLibraryFolders(IEnumerable<string> folders, bool recursive)
    {
        _settings.Current.Library.Folders = [.. folders];
        _settings.Current.Library.Recursive = recursive;
        _settings.Save();
        _library.Configure(_settings.Current.Library);
    }

    public IReadOnlyDictionary<string, string> UnreadableFiles => _library.Skipped;

    public void RetryUnreadableFiles() => _library.RetrySkipped();

    // ---- detection -------------------------------------------------------

    public void UpdateDetection(Action<DetectionConfig> change)
    {
        change(_settings.Current.Detection);
        _settings.Save();
        _detection.Configure(_settings.Current.Detection);
    }

    public void UpdateOutput(Action<OutputSettings> change)
    {
        change(_settings.Current.Output);
        _settings.Save();
        _playback.Configure(_settings.Current);
    }

    public void UpdateGeneral(Action<GeneralSettings> change)
    {
        change(_settings.Current.General);
        _settings.Save();
    }

    // ---- television ------------------------------------------------------

    public string TvStatus => _tv.Status;

    public Tv.DisplayCapabilities TvCapabilities => _tv.Controller.Capabilities;

    public bool TvControlEnabled => _tv.Controller.Capabilities != Tv.DisplayCapabilities.None;

    /// <summary>Non-null while wake attempts are suppressed after a manual power-off.</summary>
    public DateTimeOffset? TvWakeVetoedUntil => _tv.PolicyState.UserVetoUntil;

    public void UpdateTv(Action<TvSettings> change)
    {
        change(_settings.Current.Tv);
        _settings.Save();
        _tv.RebuildController();
    }

    public void WakeTv() => _tv.WakeNow();

    public void SleepTv() => _tv.SleepNow();

    public void SendTvVolume(Tv.VolumeCommand command) => _tv.SendVolume(command);

    public Task<bool> PairTvAsync(CancellationToken ct) => _tv.PairAsync(ct);

    public Task<Tv.DisplayPowerState> GetTvPowerStateAsync(CancellationToken ct) => _tv.GetPowerStateAsync(ct);

    public Task<IReadOnlyList<Tv.Samsung.SamsungDeviceInfo>> DiscoverTvsAsync(CancellationToken ct) =>
        _discovery.SweepAsync(null, ct);

    /// <summary>The panic switch: turns all television control off in one click.</summary>
    public void DisableTvControl() => UpdateTv(t => t.Provider = "none");

    /// <summary>
    /// Adds or replaces a rule for an application. Used by the live view's right-click menu,
    /// which is the fastest possible way to fix a false positive: see it, click it, done.
    /// </summary>
    public void SetRuleFor(string exeName, RuleMode mode)
    {
        List<ProcessRule> rules = _settings.Current.Detection.Rules;
        rules.RemoveAll(r => r.MatchKind == RuleMatchKind.ExeName
            && string.Equals(r.Match, exeName, StringComparison.OrdinalIgnoreCase));

        // Inserted at the front: first match wins, so a rule the user just chose has to
        // outrank the built-in one it is replacing.
        rules.Insert(0, new ProcessRule(exeName, RuleMatchKind.ExeName, mode));

        _settings.Save();
        _detection.Configure(_settings.Current.Detection);
        _log.LogInformation("Rule for {Exe} set to {Mode}.", exeName, mode);
    }

    public void ResetDetectionToDefaults()
    {
        _settings.Current.Detection = new DetectionConfig();
        _settings.Save();
        _detection.Configure(_settings.Current.Detection);
    }

    // ---- misc ------------------------------------------------------------

    public void RescanLibrary() => _library.Rescan();

    public void OpenLogFolder() => OpenInExplorer(_paths.LogDirectory);

    public void OpenSettingsFile() => OpenInExplorer(_paths.SettingsFile);

    private void OpenInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or InvalidOperationException)
        {
            _log.LogWarning(ex, "Could not open {Path}.", path);
        }
    }
}
