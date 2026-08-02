using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace NoSilence.App;

/// <summary>
/// Run-at-logon, via <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.
/// </summary>
/// <remarks>
/// A registry value rather than a shortcut in the Startup folder: it is readable and
/// repairable from inside the app, it cannot be left behind as an orphaned <c>.lnk</c>
/// pointing at an executable that has moved, and <see cref="RepairIfStale"/> can silently
/// correct the path after the app is moved or updated — which for a self-contained exe that
/// people drop wherever they like happens often.
/// </remarks>
internal sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NoSilence";

    private readonly ILogger<StartupRegistration> _log;

    public StartupRegistration(ILogger<StartupRegistration> log) => _log = log;

    private static string CommandLine => $"\"{Environment.ProcessPath}\"";

    public bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read the run-at-startup setting.");
            return false;
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                ?? throw new InvalidOperationException("could not open the Run key");

            if (enabled)
            {
                key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
                _log.LogInformation("Enabled run at startup: {Command}", CommandLine);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _log.LogInformation("Disabled run at startup.");
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.LogError(ex, "Could not change the run-at-startup setting.");
            return false;
        }
    }

    /// <summary>
    /// Rewrites the registered command if the executable has moved, so an update or a
    /// relocation does not quietly stop the app starting at logon.
    /// </summary>
    public void RepairIfStale()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string registered)
            {
                return;
            }

            if (!string.Equals(registered, CommandLine, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
                _log.LogInformation("Run-at-startup pointed at {Old}; corrected to {New}.", registered, CommandLine);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "Could not repair the run-at-startup entry.");
        }
    }
}
