using NoSilence.Interop;

namespace NoSilence.App;

/// <summary>
/// Guarantees one running NoSilence. Two instances would fight over the output device and
/// each would hear the other's music as "someone is making noise" — the exact oscillation
/// this rewrite exists to remove.
/// </summary>
/// <remarks>
/// A second launch does not just exit silently: it broadcasts a request for the running
/// instance to show its settings window, so double-clicking the exe does something useful
/// instead of appearing to do nothing.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    public const string ShowSettingsMessageName = "NoSilence.ShowSettings.9E4C1B7A";

    /// <summary>
    /// Asks a running instance to shut down cleanly. Used by <c>--quit</c>, which exists so
    /// installers, updaters and scripts have a way to stop the app that releases the audio
    /// device and saves state, rather than killing the process.
    /// </summary>
    public const string QuitMessageName = "NoSilence.Quit.9E4C1B7A";

    private const string MutexName = @"Global\NoSilence.SingleInstance.9E4C1B7A-6D2F-4E1C-9C3B-7A1D5E0F2B84";

    private Mutex? _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Returns a handle when this process owns the instance, or null when another instance
    /// is already running (in which case it has been asked to surface its settings window).
    /// </summary>
    public static SingleInstance? AcquireOrSignalExisting()
    {
        Mutex mutex;
        bool owned;

        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out owned);
        }
        catch (UnauthorizedAccessException)
        {
            // A mutex with this name exists under another account (fast user switching).
            // Treat that as "someone else owns it" rather than crashing.
            SignalExisting();
            return null;
        }

        if (!owned)
        {
            mutex.Dispose();
            SignalExisting();
            return null;
        }

        return new SingleInstance(mutex);
    }

    private static void SignalExisting() => Broadcast(ShowSettingsMessageName);

    /// <summary>Asks any running instance to shut down. Returns false if the message could not be registered.</summary>
    public static bool RequestQuit() => Broadcast(QuitMessageName);

    private static bool Broadcast(string messageName)
    {
        uint message = NativeMethods.RegisterWindowMessage(messageName);
        return message != 0 && NativeMethods.PostMessage(NativeMethods.HwndBroadcast, message, 0, 0);
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned on this thread (shutdown races). Disposing is still correct.
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
