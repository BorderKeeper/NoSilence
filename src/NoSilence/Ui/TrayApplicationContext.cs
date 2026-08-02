using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using NoSilence.App;

namespace NoSilence.Ui;

/// <summary>
/// Owns the tray icon and is the app's lifetime: <c>Application.Run</c> returns when this
/// context ends, so shutting down means calling <see cref="ExitThread"/>.
/// </summary>
/// <remarks>
/// M0 scope: the icon exists, tracks state, survives an Explorer restart and a DPI change,
/// and exits cleanly. Transport, mode/snooze, device selection and the settings window
/// arrive in M4/M5.
/// </remarks>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ILogger<TrayApplicationContext> _log;
    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly MessageWindow _messageWindow = new();
    private readonly ContextMenuStrip _menu = new();

    private TrayIconState _state = TrayIconState.Waiting;
    private string _tooltip = "NoSilence — starting up";
    private bool _shuttingDown;

    public TrayApplicationContext(ILogger<TrayApplicationContext> log)
    {
        _log = log;

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons.Get(_state),
            Text = Truncate(_tooltip),
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _notifyIcon.DoubleClick += (_, _) => ShowSettings();
        _menu.Opening += OnMenuOpening;

        _messageWindow.ShowSettingsRequested += (_, _) => ShowSettings();
        _messageWindow.TaskbarCreated += (_, _) => ReaddIcon();
        _messageWindow.QuitRequested += (_, _) => Shutdown("another process asked us to quit");
        _messageWindow.SessionEnding += (_, _) => Shutdown("Windows is ending the session");

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        BuildMenu();
        _log.LogInformation("Tray icon created.");
    }

    /// <summary>Raised when the user picks Settings. Wired up in M5.</summary>
    public event EventHandler? SettingsRequested;

    public event EventHandler? NextRequested;

    public event EventHandler? PreviousRequested;

    public event EventHandler? ReopenDeviceRequested;

    public event EventHandler? ExitRequested;

    /// <summary>Reflects the current playback state in the icon and tooltip. Call on the UI thread.</summary>
    public void Apply(Playback.PlaybackSnapshot snapshot)
    {
        (TrayIconState state, string text) = snapshot.Phase switch
        {
            Playback.PlaybackPhase.Playing => (TrayIconState.Playing, $"Playing: {snapshot.Track?.DisplayName ?? "…"}"),
            Playback.PlaybackPhase.Ducked => (TrayIconState.Ducked, snapshot.Detail ?? "Silent — something else is playing"),
            Playback.PlaybackPhase.Silenced => (TrayIconState.Disabled, snapshot.Detail ?? "Silent"),
            Playback.PlaybackPhase.NoDevice => (TrayIconState.Waiting, snapshot.Detail ?? "Waiting for the output device"),
            Playback.PlaybackPhase.Opening => (TrayIconState.Waiting, "Opening the output device…"),
            Playback.PlaybackPhase.Faulted => (TrayIconState.Error, snapshot.Detail ?? "Playback failed"),
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

        _state = state;
        _tooltip = tooltip;

        if (_shuttingDown)
        {
            return;
        }

        _notifyIcon.Icon = _icons.Get(state);
        _notifyIcon.Text = Truncate(tooltip);
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();

        var header = new ToolStripMenuItem("NoSilence") { Enabled = false };
        _menu.Items.Add(header);
        _menu.Items.Add(new ToolStripSeparator());

        var next = new ToolStripMenuItem("Next track");
        next.Click += (_, _) => NextRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(next);

        var previous = new ToolStripMenuItem("Previous track");
        previous.Click += (_, _) => PreviousRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(previous);

        var reopen = new ToolStripMenuItem("Reconnect output device");
        reopen.Click += (_, _) => ReopenDeviceRequested?.Invoke(this, EventArgs.Empty);
        _menu.Items.Add(reopen);

        _menu.Items.Add(new ToolStripSeparator());

        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => ShowSettings();
        _menu.Items.Add(settings);

        var logs = new ToolStripMenuItem("Open log folder");
        logs.Click += (_, _) => OpenLogFolder();
        _menu.Items.Add(logs);

        _menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Shutdown("user chose Exit");
        _menu.Items.Add(exit);
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_menu.Items.Count > 0)
        {
            _menu.Items[0].Text = _tooltip;
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            AppPaths paths = AppPaths.Resolve();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = paths.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open the log folder.");
        }
    }

    private void ShowSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

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
        catch (Exception ex)
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
