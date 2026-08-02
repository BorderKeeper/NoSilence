using System.Text.Json;
using Microsoft.Extensions.Logging;
using NoSilence.App;

namespace NoSilence.Settings;

/// <summary>
/// Runtime state that is not user configuration.
/// </summary>
/// <remarks>
/// Kept out of <c>settings.json</c> deliberately: none of it is meaningful to edit by hand,
/// it changes constantly, and a pairing token does not belong in a file people are
/// encouraged to open and share when reporting a problem.
/// </remarks>
internal sealed class AppState
{
    /// <summary>Samsung pairing token. Persisted so the on-screen prompt appears only once.</summary>
    public string? SamsungToken { get; set; }

    /// <summary>
    /// Wake/sleep bookkeeping. Persisted because "we were the ones who turned the television
    /// on" has to survive a restart, or a set we woke would never be turned off again.
    /// </summary>
    public Tv.TvPolicyState TvPolicy { get; set; } = new();
}

internal sealed class StateService
{
    private readonly AppPaths _paths;
    private readonly ILogger<StateService> _log;
    private readonly Lock _gate = new();

    public StateService(AppPaths paths, ILogger<StateService> log)
    {
        _paths = paths;
        _log = log;
        Current = new AppState();
    }

    public AppState Current { get; private set; }

    public AppState Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_paths.StateFile))
                {
                    Current = JsonSerializer.Deserialize<AppState>(File.ReadAllText(_paths.StateFile), JsonOptions.Default)
                        ?? new AppState();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // State is regenerable; losing it costs one extra pairing prompt at worst.
                _log.LogWarning(ex, "Could not read state.json; starting fresh.");
                Current = new AppState();
            }

            return Current;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                File.WriteAllText(_paths.StateFile, JsonSerializer.Serialize(Current, JsonOptions.Default));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _log.LogWarning(ex, "Could not save state.json.");
            }
        }
    }
}
