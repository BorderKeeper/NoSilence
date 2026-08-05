using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using NoSilence.App;
using NoSilence.Audio;
using NoSilence.Detection;
using NoSilence.Playback;
using NoSilence.Settings;
using NoSilence.Tv;

namespace NoSilence.Ui;

/// <summary>
/// Owns the tray icon and is the app's lifetime: <c>Application.Run</c> returns when this
/// context ends, so shutting down means calling <see cref="ApplicationContext.ExitThread"/>.
/// </summary>
/// <remarks>
/// A view over <see cref="AppController"/>. It renders state and calls methods; no settings
/// writing or engine poking happens in a click handler.
/// </remarks>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppController _app;
    private readonly ILogger<TrayApplicationContext> _log;
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly MessageWindow _messageWindow = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly VolumeSliderHost _volume = new();

    private ToolStripMenuItem _header = null!;
    private ToolStripMenuItem _reason = null!;
    private ToolStripMenuItem _fixOutput = null!;
    private ToolStripMenuItem _playThroughCall = null!;
    private ToolStripMenuItem _modeMenu = null!;
    private ToolStripMenuItem _snoozeMenu = null!;
    private ToolStripMenuItem _deviceMenu = null!;
    private ToolStripMenuItem _tvMenu = null!;
    private ToolStripMenuItem _cancelSnooze = null!;

    private TrayIconState _state = TrayIconState.Waiting;
    private string _tooltip = "NoSilence — starting up";
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Empty;
    private DateTimeOffset _lastBalloonAt;
    private Action? _balloonAction;
    private bool _wasInCall;
    private bool _shuttingDown;

    public TrayApplicationContext(AppController app, ILogger<TrayApplicationContext> log)
    {
        _app = app;
        _log = log;

        BuildMenu();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons.Get(_state),
            Text = Truncate(_tooltip),
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _notifyIcon.DoubleClick += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _notifyIcon.MouseUp += OnIconMouseUp;
        _notifyIcon.BalloonTipClicked += (_, _) => RunBalloonAction();
        _menu.Opening += (_, _) => RefreshMenu();

        _messageWindow.ShowSettingsRequested += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _messageWindow.TaskbarCreated += (_, _) => ReaddIcon();
        _messageWindow.QuitRequested += (_, _) => Shutdown("another process asked us to quit");
        _messageWindow.SessionEnding += (_, _) => Shutdown("Windows is ending the session");

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _log.LogInformation("Tray icon created.");
    }

    /// <summary>
    /// Raised when the user asks for the settings window. Carries
    /// <see cref="ShowLiveViewArgs"/> when it should open on the live view.
    /// </summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Marker telling the host to open the settings window on the live view.</summary>
    internal sealed class ShowLiveViewArgs : EventArgs
    {
        public static ShowLiveViewArgs Instance { get; } = new();
    }

    public event EventHandler? ExitRequested;

    // ---- state -----------------------------------------------------------

    /// <summary>Reflects the current playback state in the icon and tooltip. Call on the UI thread.</summary>
    public void Apply(PlaybackSnapshot snapshot)
    {
        _snapshot = snapshot;
        NotifyCallStarted();

        // An inaudible output outranks whatever the phase says: reporting "Playing" while
        // the room is silent is the least helpful thing the tray could do.
        if (snapshot.Warning is { } warning)
        {
            SetState(TrayIconState.Error, $"NoSilence — {warning}");
            return;
        }

        (TrayIconState state, string text) = snapshot.Phase switch
        {
            PlaybackPhase.Playing => (TrayIconState.Playing, $"Playing: {snapshot.Track?.DisplayName ?? "…"}"),
            PlaybackPhase.Ducked => (TrayIconState.Ducked, snapshot.Detail ?? "Silent — something else is playing"),
            PlaybackPhase.Silenced => (TrayIconState.Disabled, snapshot.Detail ?? "Silent"),
            PlaybackPhase.NoDevice => (TrayIconState.Waiting, snapshot.Detail ?? "Waiting for the output device"),
            PlaybackPhase.Opening => (TrayIconState.Waiting, "Opening the output device…"),
            PlaybackPhase.Faulted => (TrayIconState.Error, snapshot.Detail ?? "Playback failed"),
            _ => (TrayIconState.Disabled, snapshot.Detail ?? "Nothing to play"),
        };

        SetState(state, $"NoSilence — {text}");
    }

    public void SetState(TrayIconState state, string tooltip)
    {
        if (_state == state && string.Equals(_tooltip, tooltip, StringComparison.Ordinal))
        {
            return;
        }

        TrayIconState previous = _state;
        _state = state;
        _tooltip = tooltip;

        if (_shuttingDown)
        {
            return;
        }

        _notifyIcon.Icon = _icons.Get(state);
        _notifyIcon.Text = Truncate(tooltip);

        NotifyIfWorthIt(previous, state, tooltip);
    }

    // ---- menu ------------------------------------------------------------

    private void BuildMenu()
    {
        _header = new ToolStripMenuItem("NoSilence") { Enabled = false };
        _reason = new ToolStripMenuItem(string.Empty) { Enabled = false, Available = false };

        // Only ever visible when the output is inaudible, and first in the menu when it is.
        // Knowing the room is silent is no use without somewhere to click.
        _fixOutput = Item(string.Empty, () => _app.MakeOutputAudible());
        _fixOutput.Available = false;
        _fixOutput.Font = new System.Drawing.Font(_menu.Font, System.Drawing.FontStyle.Bold);

        // Shown only while a call is holding the music down. It expires with the call, which
        // is the difference between this and the snooze people reach for instead.
        _playThroughCall = Item("Play through this call", () => _app.PlayThroughCall());
        _playThroughCall.Available = false;

        _menu.Items.Add(_header);
        _menu.Items.Add(_reason);
        _menu.Items.Add(_fixOutput);
        _menu.Items.Add(_playThroughCall);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(Item("Next track", () => _app.NextTrack()));
        _menu.Items.Add(Item("Previous track", () => _app.PreviousTrack()));

        var volumeMenu = new ToolStripMenuItem("Volume");
        volumeMenu.DropDownItems.Add(_volume);
        _volume.VolumeChanged += (_, percent) =>
        {
            _app.SetVolume(percent);
            volumeMenu.Text = $"Volume  ({percent}%)";
        };
        _menu.Items.Add(volumeMenu);

        _menu.Items.Add(new ToolStripSeparator());

        _modeMenu = new ToolStripMenuItem("Mode");
        _modeMenu.DropDownItems.Add(ModeItem("Automatic", OperatingMode.Auto));
        _modeMenu.DropDownItems.Add(ModeItem("Always play", OperatingMode.AlwaysPlay));
        _modeMenu.DropDownItems.Add(ModeItem("Always silent", OperatingMode.AlwaysSilent));
        _menu.Items.Add(_modeMenu);

        _snoozeMenu = new ToolStripMenuItem("Snooze");
        _snoozeMenu.DropDownItems.Add(Item("15 minutes", () => _app.Snooze(TimeSpan.FromMinutes(15))));
        _snoozeMenu.DropDownItems.Add(Item("30 minutes", () => _app.Snooze(TimeSpan.FromMinutes(30))));
        _snoozeMenu.DropDownItems.Add(Item("1 hour", () => _app.Snooze(TimeSpan.FromHours(1))));
        _snoozeMenu.DropDownItems.Add(Item("2 hours", () => _app.Snooze(TimeSpan.FromHours(2))));
        _snoozeMenu.DropDownItems.Add(Item("Until I turn it back on", () => _app.SnoozeIndefinitely()));
        _snoozeMenu.DropDownItems.Add(new ToolStripSeparator());
        _cancelSnooze = Item("Cancel snooze", () => _app.CancelSnooze());
        _snoozeMenu.DropDownItems.Add(_cancelSnooze);
        _menu.Items.Add(_snoozeMenu);

        _menu.Items.Add(new ToolStripSeparator());

        // Both submenus are filled when they are opened, not when the root menu is. Building
        // them up front put a full COM enumeration of every render endpoint Windows has ever
        // seen — four of them share one friendly name on the author's machine, three of those
        // stale — on the UI thread between the right-click and the menu appearing, which was
        // visible as a lag every single time. Almost nobody opens these.
        //
        // The placeholder matters: WinForms raises DropDownOpening only for a submenu that
        // already has at least one item, and draws no arrow for an empty one.
        _deviceMenu = new ToolStripMenuItem("Output device");
        _deviceMenu.DropDownItems.Add(Loading());
        _deviceMenu.DropDownOpening += (_, _) => RefreshDeviceMenu();
        _menu.Items.Add(_deviceMenu);

        _tvMenu = new ToolStripMenuItem("Television");
        _tvMenu.DropDownItems.Add(Loading());
        _tvMenu.DropDownOpening += (_, _) => RefreshTvMenu();
        _menu.Items.Add(_tvMenu);

        _menu.Items.Add(new ToolStripSeparator());
        // Opens straight onto the live view, which answers the question far better than a
        // balloon: it names every source, its level, and whether it counts.
        _menu.Items.Add(Item("Why is it silent?…", () => SettingsRequested?.Invoke(this, ShowLiveViewArgs.Instance)));
        _menu.Items.Add(Item("Settings…", () => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        _menu.Items.Add(Item("Open log folder", () => _app.OpenLogFolder()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Exit", () => Shutdown("user chose Exit")));
    }

    private void RefreshMenu()
    {
        _header.Text = _tooltip.StartsWith("NoSilence — ", StringComparison.Ordinal)
            ? _tooltip["NoSilence — ".Length..]
            : _tooltip;

        // Only show the "why" line when there is something to explain.
        DecisionOutcome? decision = _app.LastDecision;
        bool explainable = decision is { WantsSilence: true } && _snapshot.Phase is PlaybackPhase.Ducked or PlaybackPhase.Silenced;
        _reason.Available = explainable;
        if (explainable)
        {
            _reason.Text = "    " + decision!.Reason;
        }

        _fixOutput.Available = _snapshot.Warning is not null;
        if (_fixOutput.Available)
        {
            _fixOutput.Text = $"Make {_snapshot.DeviceName ?? "the output"} audible again";
        }

        _playThroughCall.Available = _app.IsInCall && !_app.Override.PlayThroughCall;

        _volume.SetValueQuietly(_app.Settings.Output.VolumePercent);

        OverrideState state = _app.Override;
        foreach (ToolStripItem item in _modeMenu.DropDownItems)
        {
            if (item is ToolStripMenuItem mode && mode.Tag is OperatingMode value)
            {
                mode.Checked = state.Mode == value && !state.IsSnoozed(DateTimeOffset.Now);
            }
        }

        bool snoozed = state.IsSnoozed(DateTimeOffset.Now);
        _cancelSnooze.Enabled = snoozed;
        _snoozeMenu.Text = snoozed
            ? $"Snooze  (until {state.SnoozeUntil!.Value.LocalDateTime:HH:mm})"
            : "Snooze";

        // Deliberately not RefreshDeviceMenu/RefreshTvMenu — those run when their own submenu
        // opens. All that is needed here is whether the television entry appears at all, which
        // is a property read rather than a device enumeration.
        _tvMenu.Available = _app.TvControlEnabled;
    }

    private static ToolStripMenuItem Loading() => new("…") { Enabled = false };

    /// <summary>
    /// Hidden entirely when no television provider is configured, rather than shown greyed —
    /// most people will never set one up, and a permanently dead submenu is clutter.
    /// </summary>
    private void RefreshTvMenu()
    {
        _tvMenu.Available = _app.TvControlEnabled;
        if (!_tvMenu.Available)
        {
            return;
        }

        _tvMenu.DropDownItems.Clear();

        DisplayCapabilities capabilities = _app.TvCapabilities;

        if (capabilities.HasFlag(DisplayCapabilities.Wake))
        {
            _tvMenu.DropDownItems.Add(Item("Turn the television on", () => _app.WakeTv()));
        }

        if (capabilities.HasFlag(DisplayCapabilities.Sleep))
        {
            _tvMenu.DropDownItems.Add(Item("Turn the television off", () => _app.SleepTv()));
        }

        if (capabilities.HasFlag(DisplayCapabilities.Volume))
        {
            _tvMenu.DropDownItems.Add(new ToolStripSeparator());
            _tvMenu.DropDownItems.Add(Item("Volume up", () => _app.SendTvVolume(VolumeCommand.Up)));
            _tvMenu.DropDownItems.Add(Item("Volume down", () => _app.SendTvVolume(VolumeCommand.Down)));
            _tvMenu.DropDownItems.Add(Item("Mute", () => _app.SendTvVolume(VolumeCommand.ToggleMute)));
        }

        _tvMenu.DropDownItems.Add(new ToolStripSeparator());

        string status = _app.TvWakeVetoedUntil is { } veto && veto > DateTimeOffset.Now
            ? $"Waking paused until {veto.LocalDateTime:HH:mm} (you turned it off)"
            : _app.TvStatus;

        _tvMenu.DropDownItems.Add(new ToolStripMenuItem(status) { Enabled = false });

        // The panic switch. Always one click away.
        _tvMenu.DropDownItems.Add(Item("Turn off all television control", () =>
        {
            _app.DisableTvControl();
            ShowBalloon("NoSilence", "Television control is now off.", ToolTipIcon.Info, force: true);
        }));
    }

    private void RefreshDeviceMenu()
    {
        _deviceMenu.DropDownItems.Clear();

        string? selectedId = _app.Settings.Output.DeviceId;
        IReadOnlyList<AudioEndpointInfo> devices = _app.ListOutputDevices();

        foreach (AudioEndpointInfo device in devices)
        {
            // Configured-but-absent devices stay listed and greyed rather than vanishing,
            // so "why is nothing playing" has a visible answer.
            var item = new ToolStripMenuItem(device.IsActive ? device.FriendlyName : $"{device.FriendlyName}  (not connected)")
            {
                Checked = string.Equals(device.Id, selectedId, StringComparison.Ordinal),
                Tag = device,
            };

            AudioEndpointInfo captured = device;
            item.Click += (_, _) => _app.SelectOutputDevice(captured);
            _deviceMenu.DropDownItems.Add(item);
        }

        if (devices.Count == 0)
        {
            _deviceMenu.DropDownItems.Add(new ToolStripMenuItem("(no output devices found)") { Enabled = false });
        }

        _deviceMenu.DropDownItems.Add(new ToolStripSeparator());
        _deviceMenu.DropDownItems.Add(Item("Reconnect now", () => _app.ReopenDevice()));
    }

    private ToolStripMenuItem ModeItem(string text, OperatingMode mode)
    {
        var item = new ToolStripMenuItem(text) { Tag = mode, CheckOnClick = false };
        item.Click += (_, _) => _app.SetMode(mode);
        return item;
    }

    private static ToolStripMenuItem Item(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Middle-click toggles between automatic and silent — the one shortcut worth having.</summary>
    private void OnIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Middle)
        {
            return;
        }

        _app.SetMode(_app.Override.Mode == OperatingMode.AlwaysSilent ? OperatingMode.Auto : OperatingMode.AlwaysSilent);
    }

    private void ExplainCurrentDecision()
    {
        DecisionOutcome? decision = _app.LastDecision;

        string message = decision is null
            ? "Nothing has been decided yet."
            : decision.WantsSilence
                ? decision.Reason
                : $"Nothing is silencing the music.\n\n{decision.Reason}";

        if (decision is { WantsSilence: true })
        {
            IEnumerable<string> counting = decision.Contributions
                .Where(c => c.Counts)
                .Select(c => $"• {c.Source}: {c.Detail}");

            string detail = string.Join("\n", counting);
            if (!string.IsNullOrEmpty(detail))
            {
                message = detail;
            }
        }

        ShowBalloon("Why NoSilence is silent", message, ToolTipIcon.Info, force: true);
    }

    // ---- notifications ---------------------------------------------------

    /// <summary>
    /// Deliberately restrained: this is an app whose whole premise is not bothering you, so
    /// routine ducking never produces a balloon.
    /// </summary>
    private void NotifyIfWorthIt(TrayIconState previous, TrayIconState current, string tooltip)
    {
        NotificationLevel level = _app.Settings.General.Notifications;
        if (level == NotificationLevel.Off || previous == current)
        {
            return;
        }

        bool isProblem = current is TrayIconState.Error or TrayIconState.Waiting;
        if (!isProblem && level != NotificationLevel.All)
        {
            return;
        }

        string message = tooltip.Replace("NoSilence — ", string.Empty, StringComparison.Ordinal);

        // An inaudible output is the one problem the app can fix itself, so say so and make
        // the balloon the button.
        if (_snapshot.Warning is not null)
        {
            ShowBalloon("NoSilence", $"{message}\r\nClick here to fix it.", ToolTipIcon.Warning,
                onClick: () => _app.MakeOutputAudible());
            return;
        }

        ShowBalloon("NoSilence", message, isProblem ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// <summary>
    /// Says that the music has been starting and stopping too much, and opens the live view.
    /// </summary>
    /// <remarks>
    /// Call on the UI thread. The detection service raises this at most once an hour, so it
    /// needs no rate limiting of its own — and "Why is it silent?" is the right destination,
    /// because it names every source and its level rather than describing the symptom again.
    /// </remarks>
    public void NotifyFlapping(int transitions)
    {
        if (_app.Settings.General.Notifications == NotificationLevel.Off)
        {
            return;
        }

        ShowBalloon(
            "NoSilence",
            $"The music has started and stopped {transitions} times in the last hour.\r\nClick here to see what keeps triggering it.",
            ToolTipIcon.Warning,
            force: true,
            onClick: () => SettingsRequested?.Invoke(this, ShowLiveViewArgs.Instance));
    }

    /// <summary>
    /// One balloon when a call takes the music down, carrying the escape hatch with it.
    /// </summary>
    /// <remarks>
    /// The exception to the "routine ducking never produces a balloon" rule, and it earns the
    /// exception: a call is the one duck that lasts an hour, and the moment you want to
    /// overrule it is the moment it starts. Fired on the transition only, so a long meeting
    /// produces exactly one.
    /// </remarks>
    private void NotifyCallStarted()
    {
        bool inCall = _app.IsInCall;
        if (inCall == _wasInCall)
        {
            return;
        }

        _wasInCall = inCall;

        if (!inCall || _app.Settings.General.Notifications == NotificationLevel.Off)
        {
            return;
        }

        string who = _app.LastDecision?.Reason ?? "In a call";
        ShowBalloon("NoSilence", $"{who}\r\nClick here to play through it.", ToolTipIcon.Info,
            force: true, onClick: () => _app.PlayThroughCall());
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon, bool force = false, Action? onClick = null)
    {
        if (_shuttingDown)
        {
            return;
        }

        // Rate limit, or a device flapping would produce a stream of popups.
        if (!force && DateTimeOffset.Now - _lastBalloonAt < TimeSpan.FromSeconds(10))
        {
            return;
        }

        _lastBalloonAt = DateTimeOffset.Now;
        _balloonAction = onClick;
        _notifyIcon.ShowBalloonTip(5000, title, message, icon);
    }

    /// <summary>
    /// Runs whatever the last balloon offered. Cleared as it runs, so a stray click on a
    /// balloon that has already been dismissed and replaced cannot fire the old action.
    /// </summary>
    private void RunBalloonAction()
    {
        Action? action = _balloonAction;
        _balloonAction = null;
        action?.Invoke();
    }

    // ---- lifetime --------------------------------------------------------

    /// <summary>
    /// Explorer crashed and restarted, taking every tray icon with it. Toggling Visible
    /// re-registers ours; without this the app is still running but invisible forever.
    /// </summary>
    private void ReaddIcon()
    {
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Icon = _icons.Get(_state);
            _notifyIcon.Text = Truncate(_tooltip);
            _notifyIcon.Visible = true;
            _log.LogInformation("Explorer restarted; tray icon re-added.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _log.LogWarning(ex, "Failed to re-add the tray icon after Explorer restarted.");
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => RefreshIconForDpi();

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Window or UserPreferenceCategory.VisualStyle)
        {
            RefreshIconForDpi();
        }
    }

    private void RefreshIconForDpi()
    {
        if (_shuttingDown)
        {
            return;
        }

        _icons.Invalidate();
        _notifyIcon.Icon = _icons.Get(_state);
    }

    private void Shutdown(string reason)
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _log.LogInformation("Shutting down: {Reason}.", reason);
        ExitRequested?.Invoke(this, EventArgs.Empty);

        // Hide before disposing, or the icon lingers in the tray until the user hovers it.
        _notifyIcon.Visible = false;
        ExitThread();
    }

    private static string Truncate(string text) =>
        text.Length <= 127 ? text : string.Concat(text.AsSpan(0, 124), "...");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _messageWindow.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }
}
