using Microsoft.Win32;
using NoSilence.Detection;
using NoSilence.Interop;

namespace NoSilence.Signals;

/// <summary>
/// The supplementary Win32 signals: what the shell is doing, how long the machine has been
/// idle, and whether the workstation is locked.
/// </summary>
/// <remarks>
/// Each is one cheap call, and each is a supplement to the per-application audio data rather
/// than a replacement for it — see the caveats on <see cref="ReadShellActivity"/>.
/// </remarks>
internal sealed class SignalProbes : IDisposable
{
    private bool _locked;
    private bool _disposed;

    public SignalProbes()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>True while the workstation is locked. Unambiguous, unlike idle time.</summary>
    public bool WorkstationLocked => _locked;

    /// <summary>
    /// What Windows itself thinks is going on, via <c>SHQueryUserNotificationState</c>.
    /// </summary>
    /// <remarks>
    /// Chosen over comparing window rectangles to monitor bounds: one P/Invoke, no window
    /// enumeration, no multi-monitor maths, and it is the exact signal Windows uses to decide
    /// whether to suppress its own toasts.
    /// <para>
    /// The limitation is real and worth stating plainly: <b>borderless-windowed games report
    /// as ordinary windows</b>, and that is most modern games. This catches true
    /// exclusive-fullscreen and presentation mode only.
    /// </para>
    /// </remarks>
    public ShellActivity ReadShellActivity()
    {
        try
        {
            return NativeMethods.SHQueryUserNotificationState(out UserNotificationState state) == 0
                ? (ShellActivity)(int)state
                : ShellActivity.Unknown;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return ShellActivity.Unknown;
        }
    }

    /// <summary>
    /// Time since the last keyboard or mouse input.
    /// </summary>
    /// <remarks>
    /// Note what this does <em>not</em> see: gamepad input, and watching a two-hour film.
    /// Both look completely idle. That is why the idle signal is off by default.
    /// </remarks>
    public TimeSpan ReadUserIdle()
    {
        var info = new NativeMethods.LastInputInfo { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LastInputInfo>() };

        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Both values are 32-bit tick counts that wrap every ~49 days; the unchecked
        // subtraction is correct across the wrap, a signed comparison would not be.
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(idleMs);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _locked = e.Reason switch
        {
            SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff => true,
            SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon => false,
            _ => _locked,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
