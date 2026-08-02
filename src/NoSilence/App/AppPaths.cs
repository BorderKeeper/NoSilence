namespace NoSilence.App;

/// <summary>
/// Every path the app writes to. Roots at <c>%APPDATA%\NoSilence</c> normally, or next
/// to the executable in portable mode (a file named <c>nosilence.portable</c> sitting
/// beside the exe), so the app can live on a USB stick without leaving traces.
/// </summary>
internal sealed class AppPaths
{
    public const string PortableMarkerFileName = "nosilence.portable";

    private AppPaths(string root, bool portable)
    {
        Root = root;
        IsPortable = portable;
    }

    public string Root { get; }

    public bool IsPortable { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string SettingsBackupFile => Path.Combine(Root, "settings.json.bak");

    public string StateFile => Path.Combine(Root, "state.json");

    public string LogDirectory => Path.Combine(Root, "logs");

    public string LogFile => Path.Combine(LogDirectory, "nosilence-.log");

    public string DiagnosticsDirectory => Path.Combine(Root, "diagnostics");

    /// <summary>Directory containing the running executable (single-file aware).</summary>
    public static string ExeDirectory
    {
        get
        {
            // AppContext.BaseDirectory points at the extraction folder for a single-file
            // bundle, which is not where the user's exe lives; Environment.ProcessPath is.
            string? processPath = Environment.ProcessPath;
            return processPath is not null
                ? Path.GetDirectoryName(processPath) ?? AppContext.BaseDirectory
                : AppContext.BaseDirectory;
        }
    }

    public static AppPaths Resolve(string? overrideRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Create(Path.GetFullPath(overrideRoot), portable: false);
        }

        string exeDir = ExeDirectory;
        if (File.Exists(Path.Combine(exeDir, PortableMarkerFileName)))
        {
            return Create(Path.Combine(exeDir, "NoSilenceData"), portable: true);
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        return Create(Path.Combine(appData, "NoSilence"), portable: false);
    }

    private static AppPaths Create(string root, bool portable)
    {
        var paths = new AppPaths(root, portable);
        Directory.CreateDirectory(paths.Root);
        Directory.CreateDirectory(paths.LogDirectory);
        Directory.CreateDirectory(paths.DiagnosticsDirectory);
        return paths;
    }
}
