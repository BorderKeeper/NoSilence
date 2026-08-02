using System.Text.Json;
using Microsoft.Extensions.Logging;
using NoSilence.App;

namespace NoSilence.Settings;

/// <summary>
/// Loads and saves <c>settings.json</c>.
/// </summary>
/// <remarks>
/// Saving goes through a temporary file and <see cref="File.Replace(string, string, string)"/>,
/// which is atomic and leaves a <c>.bak</c> behind. That matters more than it sounds: this
/// file is written while the app is running and the machine can lose power mid-write, and a
/// half-written settings file that takes the app down on next launch is a miserable failure
/// mode for something that lives in the tray.
/// <para>Hot reload of external edits arrives in M5.</para>
/// </remarks>
internal sealed class SettingsService
{
    private readonly AppPaths _paths;
    private readonly ILogger<SettingsService> _log;
    private readonly Lock _gate = new();

    public SettingsService(AppPaths paths, ILogger<SettingsService> log)
    {
        _paths = paths;
        _log = log;
        Current = new AppSettings();
    }

    public AppSettings Current { get; private set; }

    /// <summary>True when the settings file was missing or unreadable and defaults are in use.</summary>
    public bool UsingDefaults { get; private set; }

    /// <summary>Set when we had to fall back to the backup or to defaults; surfaced in the UI.</summary>
    public string? LoadWarning { get; private set; }

    public AppSettings Load(bool reset = false)
    {
        lock (_gate)
        {
            if (reset)
            {
                Current = new AppSettings();
                UsingDefaults = true;
                LoadWarning = null;
                _log.LogInformation("Settings reset to defaults on request.");
                Save();
                return Current;
            }

            if (TryRead(_paths.SettingsFile, out AppSettings? settings, out string? error))
            {
                Current = Migrate(settings!);
                UsingDefaults = false;
                LoadWarning = null;
                _log.LogInformation("Loaded settings from {Path}.", _paths.SettingsFile);
                return Current;
            }

            if (File.Exists(_paths.SettingsFile))
            {
                _log.LogError("settings.json could not be read: {Error}", error);

                if (TryRead(_paths.SettingsBackupFile, out AppSettings? backup, out _))
                {
                    Current = Migrate(backup!);
                    UsingDefaults = false;
                    LoadWarning = $"settings.json was unreadable ({error}); recovered the previous version from settings.json.bak.";
                    _log.LogWarning("Recovered settings from the backup file.");
                    return Current;
                }

                LoadWarning = $"settings.json was unreadable ({error}) and no usable backup existed; starting from defaults.";
            }

            Current = new AppSettings();
            UsingDefaults = true;
            return Current;
        }
    }

    public void Save() => Save(Current);

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Current = settings;

            string target = _paths.SettingsFile;
            string temp = target + ".tmp";

            try
            {
                File.WriteAllText(temp, Serialize(settings));

                if (File.Exists(target))
                {
                    File.Replace(temp, target, _paths.SettingsBackupFile, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, target);
                }

                UsingDefaults = false;
                _log.LogDebug("Saved settings to {Path}.", target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log.LogError(ex, "Could not save settings to {Path}.", target);
                TryDelete(temp);
            }
        }
    }

    /// <summary>
    /// Serialises only the values that differ from the defaults, so improvements to a
    /// default reach existing configurations instead of being pinned by a file that
    /// recorded every property the first time the app exited.
    /// </summary>
    internal static string Serialize(AppSettings settings)
    {
        System.Text.Json.Nodes.JsonNode? full = System.Text.Json.Nodes.JsonNode.Parse(
            JsonSerializer.Serialize(settings, JsonOptions.Default));

        System.Text.Json.Nodes.JsonNode? defaults = System.Text.Json.Nodes.JsonNode.Parse(
            JsonSerializer.Serialize(new AppSettings(), JsonOptions.Default));

        System.Text.Json.Nodes.JsonNode? sparse = SparseJson.Strip(full, defaults);
        return sparse?.ToJsonString(JsonOptions.Default) ?? "{}";
    }

    private bool TryRead(string path, out AppSettings? settings, out string? error)
    {
        settings = null;
        error = null;

        if (!File.Exists(path))
        {
            error = "file does not exist";
            return false;
        }

        try
        {
            string json = File.ReadAllText(path);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions.Default);
            if (settings is null)
            {
                error = "file contained only 'null'";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.Message;
            return false;
        }
    }

    private AppSettings Migrate(AppSettings settings)
    {
        if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            // Written by a newer build. Keep what we understand rather than overwriting it.
            _log.LogWarning(
                "settings.json declares schema version {Found}, newer than this build understands ({Known}). Unrecognised options are preserved but ignored.",
                settings.SchemaVersion,
                AppSettings.CurrentSchemaVersion);
            return settings;
        }

        // v0 (no schemaVersion field at all) needs nothing beyond the property defaults.
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return settings;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Nothing useful to do; the stale .tmp is harmless.
        }
    }
}
