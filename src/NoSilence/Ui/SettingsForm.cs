using System.Drawing;
using System.Windows.Forms;
using NoSilence.App;
using NoSilence.Audio;
using NoSilence.Detection;
using NoSilence.Settings;

namespace NoSilence.Ui;

/// <summary>
/// The settings window.
/// </summary>
/// <remarks>
/// Built in code rather than with the designer: it keeps the layout reviewable in a diff and
/// avoids a .resx and a generated partial that nobody reads.
/// <para>
/// There is no OK/Cancel. Every change applies immediately, matching how the tray already
/// behaves, and removing a whole class of "did that take effect?" confusion. Closing hides
/// the window rather than disposing it, so reopening is instant and keeps scroll positions.
/// </para>
/// </remarks>
internal sealed class SettingsForm : Form
{
    private readonly AppController _app;
    private readonly StartupRegistration _startup;

    private ListBox _folders = null!;
    private CheckBox _recursive = null!;
    private Label _libraryStatus = null!;
    private ListView _devices = null!;
    private Label _outputStatus = null!;
    private CheckBox _runAtStartup = null!;
    private ComboBox _notifications = null!;
    private TextBox? _tvHost;
    private TextBox? _tvMac;
    private Label? _tvStatus;

    public SettingsForm(AppController app, StartupRegistration startup)
    {
        _app = app;
        _startup = startup;

        Text = "NoSilence";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 560);
        Size = new Size(880, 640);
        ShowInTaskbar = true;          // so Alt-Tab finds it
        Icon = TryLoadIcon();

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(BuildLibraryTab());
        tabs.TabPages.Add(BuildOutputTab());
        tabs.TabPages.Add(BuildDetectionTab());
        tabs.TabPages.Add(BuildTelevisionTab());
        tabs.TabPages.Add(BuildGeneralTab());
        Controls.Add(tabs);

        FormClosing += (_, e) =>
        {
            // The app lives in the tray; closing the window must not end it.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    /// <summary>Re-reads everything from the controller. Called each time the window is shown.</summary>
    public void ReloadFromSettings()
    {
        AppSettings settings = _app.Settings;

        _folders.Items.Clear();
        foreach (string folder in settings.Library.Folders)
        {
            _folders.Items.Add(folder);
        }

        _recursive.Checked = settings.Library.Recursive;
        _runAtStartup.Checked = _startup.IsEnabled();
        _notifications.SelectedItem = settings.General.Notifications;

        RefreshLibraryStatus();
        RefreshDevices();
    }

    // ---- library ---------------------------------------------------------

    private TabPage BuildLibraryTab()
    {
        var page = NewPage("Library");
        var layout = NewColumn();

        layout.Controls.Add(Heading("Music folders"));
        layout.Controls.Add(Hint("NoSilence plays everything it finds in these folders, shuffled, forever."));

        _folders = new ListBox { Width = 640, Height = 160, IntegralHeight = false };
        layout.Controls.Add(_folders);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(Button("Add folder…", AddFolder));
        buttons.Controls.Add(Button("Remove", RemoveFolder));
        buttons.Controls.Add(Button("Rescan now", () => { _app.RescanLibrary(); RefreshLibraryStatus(); }));
        layout.Controls.Add(buttons);

        _recursive = new CheckBox { Text = "Include subfolders", AutoSize = true };
        _recursive.CheckedChanged += (_, _) => ApplyFolders();
        layout.Controls.Add(_recursive);
        layout.Controls.Add(Hint("Turn this off when a folder sits near the root of a large drive — otherwise the scan walks the whole drive."));

        _libraryStatus = Hint(string.Empty);
        layout.Controls.Add(_libraryStatus);

        layout.Controls.Add(Button("Show files that could not be played", ShowUnreadable));

        page.Controls.Add(layout);
        return page;
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose a folder containing music", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folders.Items.Add(dialog.SelectedPath);
            ApplyFolders();
        }
    }

    private void RemoveFolder()
    {
        if (_folders.SelectedIndex >= 0)
        {
            _folders.Items.RemoveAt(_folders.SelectedIndex);
            ApplyFolders();
        }
    }

    private void ApplyFolders()
    {
        _app.SetLibraryFolders(_folders.Items.Cast<string>(), _recursive.Checked);
        RefreshLibraryStatus();
    }

    private void RefreshLibraryStatus()
    {
        int skipped = _app.UnreadableFiles.Count;
        _libraryStatus.Text = skipped == 0
            ? $"{_app.TrackCount} playable file(s) found."
            : $"{_app.TrackCount} playable file(s) found, {skipped} could not be read.";
    }

    private void ShowUnreadable()
    {
        IReadOnlyDictionary<string, string> skipped = _app.UnreadableFiles;

        if (skipped.Count == 0)
        {
            MessageBox.Show(this, "Every file in your folders opened successfully.", "NoSilence", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string list = string.Join(Environment.NewLine, skipped.Take(30).Select(p => $"{Path.GetFileName(p.Key)} — {p.Value}"));
        if (MessageBox.Show(this, $"{list}{Environment.NewLine}{Environment.NewLine}Try these again?", "Files that could not be played",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            _app.RetryUnreadableFiles();
            RefreshLibraryStatus();
        }
    }

    // ---- output ----------------------------------------------------------

    private TabPage BuildOutputTab()
    {
        var page = NewPage("Output");
        var layout = NewColumn();

        layout.Controls.Add(Heading("Where the music plays"));
        layout.Controls.Add(Hint("Pick the device you want background music on — usually your TV. Devices that are switched off are still listed."));

        _devices = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            Width = 780,
            Height = 240,
            HideSelection = false,
        };
        _devices.Columns.Add("Device", 340);
        _devices.Columns.Add("State", 110);
        _devices.Columns.Add("Endpoint ID", 320);
        _devices.SelectedIndexChanged += (_, _) => SelectDevice();
        layout.Controls.Add(_devices);

        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.Add(Button("Refresh", RefreshDevices));
        buttons.Controls.Add(Button("Play a test tone", PlayTestTone));
        buttons.Controls.Add(Button("Reconnect now", () => _app.ReopenDevice()));
        layout.Controls.Add(buttons);

        _outputStatus = Hint(string.Empty);
        layout.Controls.Add(_outputStatus);

        layout.Controls.Add(Spinner("Volume (%)", 0, 100, _app.Settings.Output.VolumePercent, v => _app.SetVolume(v)));
        layout.Controls.Add(Spinner("Buffer (ms)", 50, 1000, _app.Settings.Output.LatencyMs, v => _app.UpdateOutput(o => o.LatencyMs = v)));

        page.Controls.Add(layout);
        return page;
    }

    private void RefreshDevices()
    {
        _devices.BeginUpdate();
        _devices.Items.Clear();

        string? selected = _app.Settings.Output.DeviceId;

        foreach (AudioEndpointInfo device in _app.ListOutputDevices())
        {
            var item = new ListViewItem(device.FriendlyName) { Tag = device };
            item.SubItems.Add(device.DescribeState());
            item.SubItems.Add(device.Id);

            if (!device.IsActive)
            {
                item.ForeColor = SystemColors.GrayText;
            }

            if (string.Equals(device.Id, selected, StringComparison.Ordinal))
            {
                item.Selected = true;
                item.Font = new Font(_devices.Font, FontStyle.Bold);
            }

            _devices.Items.Add(item);
        }

        _devices.EndUpdate();
        RefreshOutputStatus();
    }

    private void SelectDevice()
    {
        if (_devices.SelectedItems.Count == 0 || _devices.SelectedItems[0].Tag is not AudioEndpointInfo device)
        {
            return;
        }

        if (!string.Equals(device.Id, _app.Settings.Output.DeviceId, StringComparison.Ordinal))
        {
            _app.SelectOutputDevice(device);
            RefreshOutputStatus();
        }
    }

    private void RefreshOutputStatus()
    {
        Playback.PlaybackSnapshot snapshot = _app.Playback;
        _outputStatus.Text = snapshot.Warning ?? snapshot.Detail ?? $"{snapshot.Phase}";
    }

    private void PlayTestTone()
    {
        if (_devices.SelectedItems.Count > 0 && _devices.SelectedItems[0].Tag is AudioEndpointInfo device)
        {
            _app.PlayTestTone(device.Id);
        }
    }

    // ---- detection -------------------------------------------------------

    private TabPage BuildDetectionTab()
    {
        var page = NewPage("Detection");
        var layout = NewColumn();
        DetectionConfig config = _app.Settings.Detection;

        layout.Controls.Add(Heading("When to go quiet"));
        layout.Controls.Add(Hint("NoSilence watches what every other application is playing, and ignores its own music."));

        layout.Controls.Add(Spinner("Threshold (dBFS)", -90, -10, (int)config.ThresholdDb,
            v => _app.UpdateDetection(c => c.ThresholdDb = v)));
        layout.Controls.Add(Hint("Anything quieter than this is treated as silence. −50 is a good starting point; −70 starts picking up apps that hold a silent audio stream open."));

        layout.Controls.Add(Spinner("Wait before going quiet (ms)", 250, 10000, config.MinDurationMs,
            v => _app.UpdateDetection(c => c.MinDurationMs = v)));
        layout.Controls.Add(Hint("How long another app must keep making sound before the music fades out. Short chimes and notification pings are around a second, so keep this above that."));

        layout.Controls.Add(Spinner("Wait before resuming (ms)", 0, 120000, config.ReleaseMs,
            v => _app.UpdateDetection(c => c.ReleaseMs = v)));
        layout.Controls.Add(Hint("How long everything must stay quiet before the music comes back. Longer survives ad breaks and pauses; shorter feels more responsive."));

        layout.Controls.Add(Heading("Extra signals"));
        layout.Controls.Add(Check("Go quiet while the microphone is in use", config.MicrophoneSignal,
            v => _app.UpdateDetection(c => c.MicrophoneSignal = v)));
        layout.Controls.Add(Check("Go quiet during full-screen games and presentations", config.FullscreenSignal,
            v => _app.UpdateDetection(c => c.FullscreenSignal = v)));
        layout.Controls.Add(Hint("Only catches true exclusive full screen. Most modern games run borderless-windowed and look like ordinary windows, so the per-application audio check is what does the real work."));
        layout.Controls.Add(Check("Go quiet while Focus Assist is on", config.FocusAssistSignal,
            v => _app.UpdateDetection(c => c.FocusAssistSignal = v)));
        layout.Controls.Add(Check("Go quiet while the workstation is locked", config.SilenceWhenLocked,
            v => _app.UpdateDetection(c => c.SilenceWhenLocked = v)));

        layout.Controls.Add(Button("Reset detection to defaults", () =>
        {
            _app.ResetDetectionToDefaults();
            MessageBox.Show(this, "Detection settings are back to their defaults. Reopen this window to see them.",
                "NoSilence", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }));

        layout.Controls.Add(Hint("A live view of what is making noise right now is coming next."));

        page.Controls.Add(layout);
        return page;
    }

    // ---- television ------------------------------------------------------

    private TabPage BuildTelevisionTab()
    {
        var page = NewPage("Television");
        var layout = NewColumn();
        TvSettings tv = _app.Settings.Tv;

        layout.Controls.Add(Heading("Turning the television on"));
        layout.Controls.Add(Hint(
            "A PC graphics card cannot send HDMI-CEC, so the television has to be woken over the network. " +
            "For Samsung sets that means Wake-on-LAN, which needs Network Standby enabled on the TV " +
            "(Settings > General > Network > Expert Settings) and works far more reliably over Ethernet than Wi-Fi."));

        var provider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        provider.Items.AddRange(["none", "samsung", "wol", "shell"]);
        provider.SelectedItem = tv.Provider;
        provider.SelectedIndexChanged += (_, _) =>
        {
            if (provider.SelectedItem is string value)
            {
                _app.UpdateTv(t => t.Provider = value);
                RefreshTvStatus();
            }
        };

        var providerRow = new FlowLayoutPanel { AutoSize = true };
        providerRow.Controls.Add(new Label { Text = "Method", AutoSize = true, Width = 220, Padding = new Padding(0, 6, 8, 0) });
        providerRow.Controls.Add(provider);
        layout.Controls.Add(providerRow);

        _tvHost = TextRow(layout, "Television IP address", tv.Host, v => _app.UpdateTv(t => t.Host = v));
        _tvMac = TextRow(layout, "MAC address", tv.MacAddress ?? string.Empty, v => _app.UpdateTv(t => t.MacAddress = v));
        layout.Controls.Add(Hint(
            "Leave the MAC blank to look it up automatically. Set it by hand if waking fails: a Samsung set often " +
            "reports its Wi-Fi radio's address even when it is plugged into Ethernet, and a packet sent there will never wake it."));

        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.Add(Button("Find my television…", DiscoverTvs));
        buttons.Controls.Add(Button("Pair", PairTv));
        buttons.Controls.Add(Button("Turn on", () => { _app.WakeTv(); RefreshTvStatus(); }));
        buttons.Controls.Add(Button("Turn off", () => { _app.SleepTv(); RefreshTvStatus(); }));
        layout.Controls.Add(buttons);

        _tvStatus = Hint(string.Empty);
        layout.Controls.Add(_tvStatus);

        layout.Controls.Add(Heading("When to do it automatically"));
        layout.Controls.Add(Check("Turn the television on when there is music to play", tv.Policy.WakeEnabled,
            v => _app.UpdateTv(t => t.Policy.WakeEnabled = v)));
        layout.Controls.Add(Spinner("…after wanting to play for (seconds)", 10, 3600, tv.Policy.RequireWantsToPlayForMs / 1000,
            v => _app.UpdateTv(t => t.Policy.RequireWantsToPlayForMs = v * 1000)));
        layout.Controls.Add(Hint("Long enough that a brief gap between videos can never power-cycle the television."));

        layout.Controls.Add(Check("Turn the television off when it has been silent for a while", tv.Policy.SleepEnabled,
            v => _app.UpdateTv(t => t.Policy.SleepEnabled = v)));
        layout.Controls.Add(Spinner("…after being idle for (minutes)", 1, 480, tv.Policy.SleepAfterMs / 60000,
            v => _app.UpdateTv(t => t.Policy.SleepAfterMs = v * 60000)));
        layout.Controls.Add(Check("Only turn it off if NoSilence turned it on", tv.Policy.OnlySleepIfWeWokeIt,
            v => _app.UpdateTv(t => t.Policy.OnlySleepIfWeWokeIt = v)));

        layout.Controls.Add(Spinner("Stop trying for this long after you switch it off (minutes)", 0, 720, tv.Policy.UserVetoMinutes,
            v => _app.UpdateTv(t => t.Policy.UserVetoMinutes = v)));
        layout.Controls.Add(Hint(
            "If you turn the television off by hand, NoSilence stops trying to wake it for this long — otherwise the two of you end up fighting over it."));

        layout.Controls.Add(Spinner("Never send more power commands per hour than", 1, 60, tv.Policy.MaxPowerCommandsPerHour,
            v => _app.UpdateTv(t => t.Policy.MaxPowerCommandsPerHour = v)));

        page.Controls.Add(layout);
        return page;
    }

    private void RefreshTvStatus()
    {
        if (_tvStatus is not null)
        {
            _tvStatus.Text = _app.TvStatus;
        }
    }

    private void DiscoverTvs()
    {
        UseWaitCursor = true;
        try
        {
            IReadOnlyList<Tv.Samsung.SamsungDeviceInfo> found = _app
                .DiscoverTvsAsync(CancellationToken.None).GetAwaiter().GetResult();

            if (found.Count == 0)
            {
                MessageBox.Show(this,
                    "No televisions answered.\n\nA Samsung set only answers when Network Standby is enabled:\n" +
                    "Settings > General > Network > Expert Settings > Power On with Mobile",
                    "NoSilence", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Tv.Samsung.SamsungDeviceInfo tv = found[0];
            _tvHost!.Text = tv.Ip;

            if (System.Net.IPAddress.TryParse(tv.Ip, out System.Net.IPAddress? address) &&
                Tv.WakeOnLan.TryResolveMacViaArp(address) is { } arp)
            {
                _tvMac!.Text = Tv.WakeOnLan.FormatMac(arp);
            }
            else if (tv.Mac is { } reported)
            {
                _tvMac!.Text = reported;
            }

            _app.UpdateTv(t =>
            {
                t.Provider = "samsung";
                t.Host = _tvHost.Text;
                t.MacAddress = _tvMac!.Text;
            });

            MessageBox.Show(this, $"Found {tv.Describe()}.\n\nNow use Pair, and accept the prompt on the television.",
                "NoSilence", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            UseWaitCursor = false;
            RefreshTvStatus();
        }
    }

    private void PairTv()
    {
        UseWaitCursor = true;
        try
        {
            bool paired = _app.PairTvAsync(CancellationToken.None).GetAwaiter().GetResult();
            MessageBox.Show(this,
                paired ? "Paired. The television will not ask again." : "Pairing failed. Make sure the television is switched on.",
                "NoSilence", MessageBoxButtons.OK, paired ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
            RefreshTvStatus();
        }
    }

    private TextBox TextRow(Control parent, string label, string value, Action<string> onChange)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        row.Controls.Add(new Label { Text = label, AutoSize = true, Width = 220, Padding = new Padding(0, 6, 8, 0) });

        var box = new TextBox { Text = value, Width = 260 };
        box.Leave += (_, _) => onChange(box.Text.Trim());
        row.Controls.Add(box);

        parent.Controls.Add(row);
        return box;
    }

    // ---- general ---------------------------------------------------------

    private TabPage BuildGeneralTab()
    {
        var page = NewPage("General");
        var layout = NewColumn();

        layout.Controls.Add(Heading("General"));

        _runAtStartup = new CheckBox { Text = "Start NoSilence when I log in", AutoSize = true };
        _runAtStartup.CheckedChanged += (_, _) =>
        {
            if (_startup.SetEnabled(_runAtStartup.Checked))
            {
                _app.UpdateGeneral(g => g.RunAtStartup = _runAtStartup.Checked);
            }
        };
        layout.Controls.Add(_runAtStartup);

        var notificationRow = new FlowLayoutPanel { AutoSize = true };
        notificationRow.Controls.Add(new Label { Text = "Notifications", AutoSize = true, Padding = new Padding(0, 6, 8, 0) });
        _notifications = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        _notifications.Items.AddRange([NotificationLevel.Off, NotificationLevel.ErrorsOnly, NotificationLevel.All]);
        _notifications.SelectedIndexChanged += (_, _) =>
        {
            if (_notifications.SelectedItem is NotificationLevel level)
            {
                _app.UpdateGeneral(g => g.Notifications = level);
            }
        };
        notificationRow.Controls.Add(_notifications);
        layout.Controls.Add(notificationRow);

        layout.Controls.Add(Heading("Files"));
        var fileButtons = new FlowLayoutPanel { AutoSize = true };
        fileButtons.Controls.Add(Button("Open log folder", () => _app.OpenLogFolder()));
        fileButtons.Controls.Add(Button("Open settings.json", () => _app.OpenSettingsFile()));
        layout.Controls.Add(fileButtons);
        layout.Controls.Add(Hint("settings.json records only what you have changed, so improvements to the defaults still reach you."));

        page.Controls.Add(layout);
        return page;
    }

    // ---- small helpers ---------------------------------------------------

    private static TabPage NewPage(string title) => new(title) { Padding = new Padding(16), AutoScroll = true, UseVisualStyleBackColor = true };

    private static FlowLayoutPanel NewColumn() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
    };

    private static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold),
        Margin = new Padding(0, 14, 0, 6),
    };

    /// <summary>
    /// Explanatory text under a control. Auto-sizes with a width cap so it wraps and grows
    /// instead of being clipped — a fixed height silently truncated the longer notes.
    /// </summary>
    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(780, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 0, 10),
    };

    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 4, 8, 8) };
        button.Click += (_, _) => action();
        return button;
    }

    private static CheckBox Check(string text, bool value, Action<bool> onChange)
    {
        var box = new CheckBox { Text = text, AutoSize = true, Checked = value, Margin = new Padding(0, 2, 0, 2) };
        box.CheckedChanged += (_, _) => onChange(box.Checked);
        return box;
    }

    private static Control Spinner(string label, int min, int max, int value, Action<int> onChange)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        row.Controls.Add(new Label { Text = label, AutoSize = true, Width = 220, Padding = new Padding(0, 6, 8, 0) });

        var spinner = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            Increment = Math.Max(1, (max - min) / 100),
            Width = 110,
        };

        spinner.ValueChanged += (_, _) => onChange((int)spinner.Value);
        row.Controls.Add(spinner);
        return row;
    }

    private static Icon? TryLoadIcon()
    {
        try
        {
            return Environment.ProcessPath is { } path ? Icon.ExtractAssociatedIcon(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException)
        {
            return null;
        }
    }
}
