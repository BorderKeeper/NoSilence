using System.Windows.Forms;
using NoSilence.Interop;

namespace NoSilence.App;

/// <summary>
/// A hidden top-level window that receives the broadcast messages a tray app needs.
/// </summary>
/// <remarks>
/// Intentionally a real (if never-shown) window rather than a message-only one:
/// message-only windows do not receive <c>HWND_BROADCAST</c> messages, which is exactly
/// how <see cref="SingleInstance"/> and Explorer's <c>TaskbarCreated</c> reach us.
/// </remarks>
internal sealed class MessageWindow : NativeWindow, IDisposable
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;

    private readonly uint _showSettingsMessage = NativeMethods.RegisterWindowMessage(SingleInstance.ShowSettingsMessageName);
    private readonly uint _quitMessage = NativeMethods.RegisterWindowMessage(SingleInstance.QuitMessageName);

    /// <summary>
    /// Explorer broadcasts this after it restarts. Tray icons are lost when Explorer dies,
    /// and an app that does not re-add its icon simply disappears — a bug most tray apps ship.
    /// </summary>
    private readonly uint _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

    public MessageWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "NoSilence.MessageWindow",
            ClassName = null,
            Style = 0,          // not WS_VISIBLE
            ExStyle = 0x00000080, // WS_EX_TOOLWINDOW: keep it out of Alt-Tab
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0,
            Parent = 0,
        });
    }

    /// <summary>Another instance asked us to surface the settings window.</summary>
    public event EventHandler? ShowSettingsRequested;

    /// <summary>Explorer restarted; the tray icon must be re-added.</summary>
    public event EventHandler? TaskbarCreated;

    /// <summary>Windows is logging off or shutting down. Save and stop cleanly.</summary>
    public event EventHandler? SessionEnding;

    /// <summary>Another process ran <c>--quit</c>.</summary>
    public event EventHandler? QuitRequested;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == _showSettingsMessage && _showSettingsMessage != 0)
        {
            ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
            m.Result = 1;
            return;
        }

        if (m.Msg == _quitMessage && _quitMessage != 0)
        {
            QuitRequested?.Invoke(this, EventArgs.Empty);
            m.Result = 1;
            return;
        }

        if (m.Msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            TaskbarCreated?.Invoke(this, EventArgs.Empty);
            return;
        }

        switch (m.Msg)
        {
            case WmQueryEndSession:
                SessionEnding?.Invoke(this, EventArgs.Empty);
                m.Result = 1; // we never block shutdown
                return;

            case WmEndSession:
                SessionEnding?.Invoke(this, EventArgs.Empty);
                break;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            DestroyHandle();
        }
    }
}
