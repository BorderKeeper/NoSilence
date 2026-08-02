using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using NoSilence.Interop;

namespace NoSilence.Audio;

/// <summary>
/// Maps an audio session to the executable behind it.
/// </summary>
/// <remarks>
/// Resolution order matters, because the obvious approach does not work:
/// <list type="number">
/// <item><b>Parse the session identifier.</b> WASAPI embeds the full image path in it
/// (<c>…|\Device\HarddiskVolume4\…\chrome.exe%b{…}</c>). Free, needs no handle, and works
/// for elevated processes.</item>
/// <item><b><c>QueryFullProcessImageName</c></b> with <c>PROCESS_QUERY_LIMITED_INFORMATION</c>.
/// The usual <c>Process.MainModule</c> throws Access Denied across an elevation boundary,
/// which is precisely where a game or a launcher tends to live.</item>
/// <item><b><c>Process.ProcessName</c></b>, last resort.</item>
/// </list>
/// <para>
/// Cached by session instance ID rather than by process ID: session IDs are unique for the
/// life of the session, so a recycled PID can never hand back the wrong name.
/// </para>
/// </remarks>
internal sealed class ProcessInfoCache
{
    private const int MaxEntries = 512;

    private readonly Dictionary<string, string> _bySession = new(StringComparer.Ordinal);
    private readonly ILogger<ProcessInfoCache> _log;

    public ProcessInfoCache(ILogger<ProcessInfoCache> log) => _log = log;

    public string Resolve(string sessionInstanceId, string? sessionIdentifier, uint processId)
    {
        if (_bySession.TryGetValue(sessionInstanceId, out string? cached))
        {
            return cached;
        }

        string name = FromSessionIdentifier(sessionIdentifier)
            ?? FromProcessHandle(processId)
            ?? FromProcessName(processId)
            ?? (processId == 0 ? "(system sounds)" : $"pid {processId}");

        if (_bySession.Count >= MaxEntries)
        {
            // Sessions are transient; a full cache means something is churning them, and
            // dropping the lot is cheaper than tracking ages we would never look at.
            _bySession.Clear();
        }

        _bySession[sessionInstanceId] = name;
        return name;
    }

    public void Forget(string sessionInstanceId) => _bySession.Remove(sessionInstanceId);

    public void Clear() => _bySession.Clear();

    private static string? FromSessionIdentifier(string? sessionIdentifier)
    {
        if (string.IsNullOrEmpty(sessionIdentifier))
        {
            return null;
        }

        // Trim the trailing "%b{guid}" that WASAPI appends, then take the file name.
        int suffix = sessionIdentifier.IndexOf("%b", StringComparison.Ordinal);
        ReadOnlySpan<char> span = suffix >= 0 ? sessionIdentifier.AsSpan(0, suffix) : sessionIdentifier.AsSpan();

        int slash = span.LastIndexOf('\\');
        if (slash < 0 || slash == span.Length - 1)
        {
            return null;
        }

        ReadOnlySpan<char> name = span[(slash + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.ToString() : null;
    }

    private string? FromProcessHandle(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        nint handle = NativeMethods.OpenProcess(NativeMethods.ProcessAccess.QueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            uint size = (uint)buffer.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? Path.GetFileName(buffer.ToString())
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            _log.LogTrace(ex, "Could not read the image name for pid {Pid}.", processId);
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string? FromProcessName(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited between enumeration and here.
            return null;
        }
    }
}
