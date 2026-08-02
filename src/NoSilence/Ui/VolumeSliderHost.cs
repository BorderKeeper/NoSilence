using System.Windows.Forms;

namespace NoSilence.Ui;

/// <summary>
/// A volume slider that lives inside the tray menu.
/// </summary>
/// <remarks>
/// Hosted in a <see cref="ToolStripControlHost"/> so the volume can be set without opening a
/// settings window — for a background music app it is the one control anyone reaches for
/// regularly.
/// <para>
/// Scroll and arrow keys are forwarded rather than left to the menu, which would otherwise
/// treat them as navigation and move the selection off the slider mid-drag.
/// </para>
/// </remarks>
internal sealed class VolumeSliderHost : ToolStripControlHost
{
    private readonly TrackBar _bar;

    public VolumeSliderHost()
        : base(new TrackBar())
    {
        _bar = (TrackBar)Control;
        _bar.Minimum = 0;
        _bar.Maximum = 100;
        _bar.TickFrequency = 10;
        _bar.SmallChange = 2;
        _bar.LargeChange = 10;
        _bar.AutoSize = false;
        _bar.Width = 180;
        _bar.Height = 30;

        AutoSize = false;
        Size = new System.Drawing.Size(180, 30);

        _bar.ValueChanged += (_, _) =>
        {
            if (!_suppress)
            {
                VolumeChanged?.Invoke(this, _bar.Value);
            }
        };

        _bar.MouseWheel += (_, e) =>
        {
            _bar.Value = Math.Clamp(_bar.Value + (e.Delta > 0 ? _bar.SmallChange : -_bar.SmallChange), _bar.Minimum, _bar.Maximum);
            ((HandledMouseEventArgs)e).Handled = true;
        };
    }

    private bool _suppress;

    /// <summary>Raised as the user drags, with the new percentage.</summary>
    public event EventHandler<int>? VolumeChanged;

    /// <summary>Updates the displayed value without raising <see cref="VolumeChanged"/>.</summary>
    public void SetValueQuietly(int percent)
    {
        _suppress = true;
        _bar.Value = Math.Clamp(percent, _bar.Minimum, _bar.Maximum);
        _suppress = false;
    }
}
