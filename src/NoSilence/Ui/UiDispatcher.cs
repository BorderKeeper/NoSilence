using System.Windows.Forms;

namespace NoSilence.Ui;

/// <summary>
/// Marshals work onto the UI thread.
/// </summary>
/// <remarks>
/// The audio engine raises its events on its own MTA thread, and touching a WinForms
/// control from there is undefined behaviour that usually manifests as a rare, unexplained
/// crash rather than an immediate one. Everything crossing that boundary goes through here.
/// <para>
/// It marshals through a hidden <see cref="Control"/> rather than
/// <see cref="SynchronizationContext.Current"/>: a tray-only app may create no forms at
/// all, in which case the WinForms synchronisation context is never installed and
/// <c>Current</c> is null. Forcing a handle here also installs it for everyone else.
/// </para>
/// <para>Must be constructed on the UI thread.</para>
/// </remarks>
internal sealed class UiDispatcher : IDisposable
{
    private readonly Control _marshal;
    private bool _disposed;

    public UiDispatcher()
    {
        _marshal = new Control();
        _ = _marshal.Handle;   // forces handle creation on the calling (UI) thread
    }

    public void Post(Action work)
    {
        if (_disposed)
        {
            return;
        }

        if (!_marshal.IsHandleCreated || !_marshal.InvokeRequired)
        {
            work();
            return;
        }

        try
        {
            _marshal.BeginInvoke(work);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The UI is shutting down; dropping a state update is the right outcome.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _marshal.Dispose();
    }
}
