using System.Drawing;
using System.Windows.Forms;
using NoSilence.Detection;

namespace NoSilence.Ui;

/// <summary>
/// The live "what is making noise right now" view.
/// </summary>
/// <remarks>
/// The debugging surface for the whole heuristic. Rows that do <em>not</em> count are shown
/// greyed rather than hidden — hiding them is precisely what makes a heuristic impossible to
/// reason about, because the interesting question is usually "why is that <em>not</em>
/// counting?" rather than "what is?".
/// <para>
/// Owner-drawn and refreshed at 4 Hz. A plain ListView flickers badly at that rate, so it is
/// double-buffered and rows are updated in place rather than rebuilt.
/// </para>
/// </remarks>
internal sealed class LiveSessionView : UserControl
{
    private readonly ListView _list;
    private readonly Label _decision;
    private readonly TimelineStrip _timeline;

    private DecisionOutcome? _last;

    public LiveSessionView()
    {
        Dock = DockStyle.Fill;

        _decision = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10f, FontStyle.Bold),
            Padding = new Padding(4, 4, 4, 0),
            Text = "Waiting…",
        };

        _timeline = new TimelineStrip { Dock = DockStyle.Top, Height = 26 };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };

        _list.Columns.Add("Application", 190);
        _list.Columns.Add("Level", 70, HorizontalAlignment.Right);
        _list.Columns.Add("Sound", 120);
        _list.Columns.Add("Sustained", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Rule", 110);
        _list.Columns.Add("Counts?", 60);
        _list.Columns.Add("Output", 180);

        EnableDoubleBuffering(_list);

        _list.ContextMenuStrip = BuildRowMenu();

        Controls.Add(_list);
        Controls.Add(_timeline);
        Controls.Add(_decision);
    }

    /// <summary>Raised when the user asks for a rule to be added for an application.</summary>
    public event EventHandler<(string ExeName, RuleMode Mode)>? RuleRequested;

    /// <summary>Feeds a new decision in. Call on the UI thread.</summary>
    public void Update(DecisionOutcome outcome)
    {
        _last = outcome;

        _decision.Text = outcome.WantsSilence
            ? $"SILENT — {outcome.Reason}"
            : $"PLAYING — {outcome.Reason}";
        _decision.ForeColor = outcome.WantsSilence ? Color.FromArgb(0xB0, 0x4A, 0x00) : Color.FromArgb(0x1E, 0x6B, 0x2E);

        _timeline.Push(outcome.WantsSilence);

        IReadOnlyList<TriggerContribution> rows = [.. outcome.Contributions
            .OrderByDescending(c => c.Counts)
            .ThenByDescending(c => c.PeakDbfs ?? c.Dbfs ?? double.MinValue)];

        _list.BeginUpdate();
        try
        {
            // Rows are reused rather than cleared and rebuilt: at 4 Hz a rebuild loses the
            // selection and flickers even with double buffering.
            while (_list.Items.Count > rows.Count)
            {
                _list.Items.RemoveAt(_list.Items.Count - 1);
            }

            for (int i = 0; i < rows.Count; i++)
            {
                TriggerContribution row = rows[i];

                if (i >= _list.Items.Count)
                {
                    var item = new ListViewItem(string.Empty);
                    for (int c = 1; c < _list.Columns.Count; c++)
                    {
                        item.SubItems.Add(string.Empty);
                    }

                    _list.Items.Add(item);
                }

                ApplyRow(_list.Items[i], row);
            }
        }
        finally
        {
            _list.EndUpdate();
        }
    }

    private static void ApplyRow(ListViewItem item, TriggerContribution row)
    {
        double? level = row.Dbfs;

        item.Text = row.Source;
        item.SubItems[1].Text = level is { } db && db > PeakMath.MinDbfs ? $"{db:F1} dB" : "silent";
        item.SubItems[2].Text = Meter(level);
        item.SubItems[3].Text = row.SustainedMs > 0 ? $"{row.SustainedMs / 1000d:F1} s" : string.Empty;
        item.SubItems[4].Text = row.Rule ?? "default";
        item.SubItems[5].Text = row.Counts ? "YES" : "no";
        item.SubItems[6].Text = row.Endpoint ?? string.Empty;
        item.Tag = row;

        item.ForeColor = row.Counts ? Color.FromArgb(0xB0, 0x4A, 0x00) : SystemColors.GrayText;
        item.Font = new Font(SystemFonts.MessageBoxFont!, row.Counts ? FontStyle.Bold : FontStyle.Regular);
    }

    /// <summary>A text bar, so the level is readable at a glance without owner-drawing cells.</summary>
    private static string Meter(double? dbfs)
    {
        if (dbfs is not { } db || db <= PeakMath.MinDbfs)
        {
            return "········";
        }

        // -70..0 dBFS across eight cells: the useful range for deciding a threshold.
        int filled = (int)Math.Round(Math.Clamp((db + 70d) / 70d, 0d, 1d) * 8);
        return new string('█', filled) + new string('·', 8 - filled);
    }

    private ContextMenuStrip BuildRowMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(RuleItem("Never let this app silence the music", RuleMode.Ignore));
        menu.Items.Add(RuleItem("Only after 4 seconds (chat apps)", RuleMode.Tolerant));
        menu.Items.Add(RuleItem("Silence the music immediately", RuleMode.AlwaysTrigger));
        menu.Items.Add(RuleItem("Use the normal rules", RuleMode.Trigger));

        menu.Opening += (_, e) =>
        {
            // Nothing selected, or a row with no real process behind it.
            if (SelectedExeName() is null)
            {
                e.Cancel = true;
            }
        };

        return menu;
    }

    private ToolStripMenuItem RuleItem(string text, RuleMode mode)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            if (SelectedExeName() is { } exe)
            {
                RuleRequested?.Invoke(this, (exe, mode));
            }
        };

        return item;
    }

    /// <summary>
    /// The row shows a friendly name, but a rule has to match an executable. The contribution
    /// carries the source label; anything without a usable one is not actionable.
    /// </summary>
    private string? SelectedExeName()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not TriggerContribution row)
        {
            return null;
        }

        return row.Source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? row.Source : null;
    }

    private static void EnableDoubleBuffering(Control control) =>
        typeof(Control)
            .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(control, true);

    /// <summary>
    /// Sixty seconds of decisions, one pixel column per tick. Flapping is far easier to see
    /// than to describe, and this makes it obvious at a glance.
    /// </summary>
    private sealed class TimelineStrip : Panel
    {
        private const int Capacity = 240;   // 60 s at 4 Hz

        private readonly Queue<bool> _history = new(Capacity);

        public TimelineStrip()
        {
            DoubleBuffered = true;
            BackColor = SystemColors.ControlLight;
        }

        public void Push(bool silent)
        {
            if (_history.Count >= Capacity)
            {
                _history.Dequeue();
            }

            _history.Enqueue(silent);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (_history.Count == 0)
            {
                return;
            }

            float width = Math.Max(1f, (float)Width / Capacity);
            using var playing = new SolidBrush(Color.FromArgb(0x4C, 0x9E, 0x5A));
            using var silent = new SolidBrush(Color.FromArgb(0xD0, 0x7A, 0x30));

            int index = 0;
            foreach (bool isSilent in _history)
            {
                e.Graphics.FillRectangle(isSilent ? silent : playing, index * width, 0, width + 1, Height);
                index++;
            }

            using var pen = new Pen(SystemColors.ControlDark);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
