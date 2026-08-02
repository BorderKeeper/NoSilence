using System.Runtime.InteropServices;

namespace NoSilence.Interop;

/// <summary>All P/Invoke lives here so the rest of the app stays readable.</summary>
internal static partial class NativeMethods
{
    private const string User32 = "user32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Shell32 = "shell32.dll";

    /// <summary>Broadcast target for <see cref="PostMessage"/>.</summary>
    public static readonly nint HwndBroadcast = 0xFFFF;

    public const int AttachParentProcess = -1;

    // ---- Window messages -------------------------------------------------

    [LibraryImport(User32, EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string lpString);

    [LibraryImport(User32, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    // ---- Console attach (for --diagnose et al. from a WinExe) -------------

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(int dwProcessId);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllocConsole();

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FreeConsole();

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial nint GetConsoleWindow();

    // ---- Icons -----------------------------------------------------------

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint hIcon);

    // ---- Shell / user activity (used from M3 onward) ----------------------

    /// <summary>
    /// The signal Windows itself uses to decide whether to suppress toasts. One call,
    /// no window enumeration, no monitor-bounds maths.
    /// </summary>
    [LibraryImport(Shell32)]
    public static partial int SHQueryUserNotificationState(out UserNotificationState state);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLastInputInfo(ref LastInputInfo plii);

    [StructLayout(LayoutKind.Sequential)]
    public struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    // ---- Process identity (works across elevation, unlike Process.MainModule) ----

    [Flags]
    public enum ProcessAccess : uint
    {
        QueryLimitedInformation = 0x1000,
    }

    [LibraryImport(Kernel32, SetLastError = true)]
    public static partial nint OpenProcess(ProcessAccess desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    // Classic DllImport: the source-generated marshaller has no support for the
    // caller-allocated StringBuilder buffer this API expects.
    [DllImport(Kernel32, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(nint hProcess, uint flags, System.Text.StringBuilder exeName, ref uint size);

    // ---- ARP (used by the TV wake-on-LAN path in M8) ---------------------

    [LibraryImport("iphlpapi.dll", EntryPoint = "SendARP")]
    public static partial int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physicalAddrLen);
}

/// <summary>Return values of <see cref="NativeMethods.SHQueryUserNotificationState"/>.</summary>
internal enum UserNotificationState
{
    /// <summary>Screen saver running or machine locked.</summary>
    NotPresent = 1,

    /// <summary>A full-screen (non-D3D) window is running.</summary>
    Busy = 2,

    /// <summary>A full-screen D3D-exclusive application is running — typically a game.</summary>
    RunningD3DFullScreen = 3,

    /// <summary>Presentation mode.</summary>
    PresentationMode = 4,

    /// <summary>Nothing special; notifications are fine.</summary>
    AcceptsNotifications = 5,

    /// <summary>Focus Assist / quiet hours.</summary>
    QuietTime = 6,

    /// <summary>A Windows Store app is running full screen.</summary>
    AppRunningFullScreen = 7,
}
