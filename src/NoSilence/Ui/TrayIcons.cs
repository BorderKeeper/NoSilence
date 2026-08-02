using System.Drawing;
using System.Windows.Forms;
using NoSilence.Interop;

namespace NoSilence.Ui;

internal enum TrayIconState
{
    /// <summary>Music is audible.</summary>
    Playing,

    /// <summary>Something else is making noise; we have faded out.</summary>
    Ducked,

    /// <summary>The configured output device is not present — typically the TV is off.</summary>
    Waiting,

    /// <summary>Snoozed, or forced to stay silent.</summary>
    Disabled,

    /// <summary>Empty library, unreadable files, or repeated device failures.</summary>
    Error,
}

/// <summary>
/// Renders the tray icon for each state at the size the shell actually wants.
/// </summary>
/// <remarks>
/// Icons are drawn rather than shipped as assets because WinForms' <c>NotifyIcon</c> does
/// not rescale: hand it a 16px icon on a 150% display and it looks like a smudge. Drawing
/// at <see cref="SystemInformation.SmallIconSize"/> — and re-drawing when that changes —
/// is the only way to look right on mixed-DPI setups.
/// </remarks>
internal sealed class TrayIcons : IDisposable
{
    private static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF);
    private static readonly Color Muted = Color.FromArgb(0x8A, 0x90, 0x9A);
    private static readonly Color Ghost = Color.FromArgb(0x70, 0x8A, 0x90, 0x9A);
    private static readonly Color Amber = Color.FromArgb(0xE0, 0xA1, 0x06);
    private static readonly Color Danger = Color.FromArgb(0xE5, 0x47, 0x4D);

    private readonly Dictionary<(TrayIconState State, int Size), Icon> _cache = [];
    private readonly List<nint> _handles = [];
    private bool _disposed;

    /// <summary>The size the shell wants right now. Re-read after a DPI or theme change.</summary>
    public static int CurrentSize
    {
        get
        {
            Size size = SystemInformation.SmallIconSize;
            return Math.Clamp(Math.Max(size.Width, 16), 16, 64);
        }
    }

    public Icon Get(TrayIconState state) => Get(state, CurrentSize);

    public Icon Get(TrayIconState state, int size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_cache.TryGetValue((state, size), out Icon? cached))
        {
            return cached;
        }

        Icon icon = Render(state, size);
        _cache[(state, size)] = icon;
        return icon;
    }

    /// <summary>Drops cached icons so the next <see cref="Get(TrayIconState)"/> re-renders at a new DPI.</summary>
    public void Invalidate()
    {
        foreach (Icon icon in _cache.Values)
        {
            icon.Dispose();
        }

        _cache.Clear();
        DestroyHandles();
    }

    private Icon Render(TrayIconState state, int size)
    {
        // A three-step opacity ramp — accent, solid grey, ghost — plus a coloured dot for
        // the two states that need attention. A mute slash was tried and rejected: at 16px
        // it cuts straight through the note and the glyph stops being legible.
        (Color note, Color? badge) = state switch
        {
            TrayIconState.Playing => (Accent, (Color?)null),
            TrayIconState.Ducked => (Muted, null),
            TrayIconState.Waiting => (Muted, Amber),
            TrayIconState.Disabled => (Ghost, null),
            TrayIconState.Error => (Muted, Danger),
            _ => (Accent, null),
        };

        using Bitmap bmp = IconFactory.RenderNote(size, Color.Transparent, note, badge);

        // GetHicon allocates an HICON that Icon does not own; it has to be destroyed
        // explicitly or the app leaks a GDI handle on every DPI change.
        nint handle = bmp.GetHicon();
        _handles.Add(handle);
        using var owned = Icon.FromHandle(handle);
        return (Icon)owned.Clone();
    }

    private void DestroyHandles()
    {
        foreach (nint handle in _handles)
        {
            NativeMethods.DestroyIcon(handle);
        }

        _handles.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Icon icon in _cache.Values)
        {
            icon.Dispose();
        }

        _cache.Clear();
        DestroyHandles();
    }
}
