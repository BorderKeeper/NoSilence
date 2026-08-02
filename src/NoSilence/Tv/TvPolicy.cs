using NoSilence.Detection;

namespace NoSilence.Tv;

internal enum TvAction
{
    None,
    Wake,
    Sleep,
}

/// <summary>Everything the policy is allowed to look at.</summary>
internal sealed record TvPolicyInput(
    DateTimeOffset Now,
    bool WantsToPlay,
    bool OutputEndpointPresent,
    bool LibraryHasTracks,
    OperatingMode Mode,
    bool Snoozed,
    DisplayCapabilities Capabilities);

internal sealed class TvPolicyConfig
{
    public bool WakeEnabled { get; set; }

    /// <summary>
    /// How long the engine must continuously want to play before the TV is woken.
    /// </summary>
    /// <remarks>
    /// Two minutes. A twenty-second lull between videos must never power-cycle a television.
    /// </remarks>
    public int RequireWantsToPlayForMs { get; set; } = 120000;

    /// <summary>Minimum gap between power commands of any kind.</summary>
    public int CooldownMs { get; set; } = 300000;

    /// <summary>Hard circuit breaker. Nothing gets to send more than this in an hour.</summary>
    public int MaxPowerCommandsPerHour { get; set; } = 6;

    /// <summary>
    /// How long to stop trying after the user appears to have switched the TV off by hand.
    /// </summary>
    public int UserVetoMinutes { get; set; } = 60;

    public bool SleepEnabled { get; set; }

    /// <summary>How long the music must have been silent before powering the TV down.</summary>
    public int SleepAfterMs { get; set; } = 1800000;

    /// <summary>Never power off a television the user turned on themselves.</summary>
    public bool OnlySleepIfWeWokeIt { get; set; } = true;
}

/// <summary>Mutable state; persisted so it survives a restart.</summary>
internal sealed class TvPolicyState
{
    /// <summary>Since when the engine has continuously wanted to play. Null if it does not.</summary>
    public DateTimeOffset? WantsToPlaySince { get; set; }

    /// <summary>Since when the output has been present but silent. Drives sleep.</summary>
    public DateTimeOffset? IdleSince { get; set; }

    public DateTimeOffset? LastPowerCommandAt { get; set; }

    /// <summary>Suppresses wake attempts after the user switched the TV off deliberately.</summary>
    public DateTimeOffset? UserVetoUntil { get; set; }

    /// <summary>
    /// True when this app was the one that turned the TV on. Persisted, or a restart would
    /// leave a television we woke running forever.
    /// </summary>
    public bool WeWokeIt { get; set; }

    /// <summary>Timestamps of recent power commands, for the hourly cap.</summary>
    public List<DateTimeOffset> RecentPowerCommands { get; set; } = [];
}

/// <summary>
/// Decides whether to turn the television on or off. Pure, and unit tested, because the
/// failure mode here is a television switching itself on in the middle of the night.
/// </summary>
internal static class TvPolicy
{
    public static TvAction Decide(TvPolicyInput input, TvPolicyConfig config, TvPolicyState state)
    {
        Track(input, state);
        PruneCommandHistory(input.Now, state);

        if (ShouldWake(input, config, state))
        {
            return TvAction.Wake;
        }

        return ShouldSleep(input, config, state) ? TvAction.Sleep : TvAction.None;
    }

    private static void Track(TvPolicyInput input, TvPolicyState state)
    {
        if (input.WantsToPlay)
        {
            state.WantsToPlaySince ??= input.Now;
        }
        else
        {
            state.WantsToPlaySince = null;
        }

        // "Idle" for sleep purposes means the device is there and we are not using it.
        if (input.OutputEndpointPresent && !input.WantsToPlay)
        {
            state.IdleSince ??= input.Now;
        }
        else
        {
            state.IdleSince = null;
        }
    }

    private static bool ShouldWake(TvPolicyInput input, TvPolicyConfig config, TvPolicyState state)
    {
        if (!config.WakeEnabled || !input.Capabilities.HasFlag(DisplayCapabilities.Wake))
        {
            return false;
        }

        // If the endpoint is already there, the TV is on and there is nothing to do. Never
        // send a power command that disagrees with what the endpoint is telling us.
        if (input.OutputEndpointPresent || !input.LibraryHasTracks)
        {
            return false;
        }

        if (input.Snoozed || input.Mode == OperatingMode.AlwaysSilent)
        {
            return false;
        }

        if (state.WantsToPlaySince is not { } since || (input.Now - since).TotalMilliseconds < config.RequireWantsToPlayForMs)
        {
            return false;
        }

        return !IsBlocked(input.Now, config, state);
    }

    private static bool ShouldSleep(TvPolicyInput input, TvPolicyConfig config, TvPolicyState state)
    {
        if (!config.SleepEnabled || !input.Capabilities.HasFlag(DisplayCapabilities.Sleep))
        {
            return false;
        }

        if (!input.OutputEndpointPresent)
        {
            return false;   // already off
        }

        if (config.OnlySleepIfWeWokeIt && !state.WeWokeIt)
        {
            return false;
        }

        if (state.IdleSince is not { } idle || (input.Now - idle).TotalMilliseconds < config.SleepAfterMs)
        {
            return false;
        }

        return !IsBlocked(input.Now, config, state);
    }

    private static bool IsBlocked(DateTimeOffset now, TvPolicyConfig config, TvPolicyState state)
    {
        if (state.UserVetoUntil is { } veto && now < veto)
        {
            return true;
        }

        if (state.LastPowerCommandAt is { } last && (now - last).TotalMilliseconds < config.CooldownMs)
        {
            return true;
        }

        return state.RecentPowerCommands.Count >= config.MaxPowerCommandsPerHour;
    }

    /// <summary>Records that a power command was issued, for the cooldown and the hourly cap.</summary>
    public static void RecordPowerCommand(DateTimeOffset now, TvPolicyState state)
    {
        state.LastPowerCommandAt = now;
        state.RecentPowerCommands.Add(now);
        state.WantsToPlaySince = null;
        state.IdleSince = null;
    }

    /// <summary>
    /// The output device vanished while we were using it, and we did not ask for that.
    /// </summary>
    /// <remarks>
    /// The single most important rule here. Without it, switching the TV off by hand starts a
    /// fight: the app notices it wants to play, wakes the TV again, and the user turns it off
    /// again. An hour of silence after a manual power-off is the polite interpretation.
    /// </remarks>
    public static void NoteUnexpectedDisappearance(DateTimeOffset now, TvPolicyConfig config, TvPolicyState state)
    {
        state.UserVetoUntil = now.AddMinutes(config.UserVetoMinutes);
        state.WeWokeIt = false;
        state.WantsToPlaySince = null;
    }

    /// <summary>Clears the veto — the user explicitly asked for a wake.</summary>
    public static void ClearVeto(TvPolicyState state) => state.UserVetoUntil = null;

    private static void PruneCommandHistory(DateTimeOffset now, TvPolicyState state)
    {
        if (state.RecentPowerCommands.Count == 0)
        {
            return;
        }

        state.RecentPowerCommands.RemoveAll(at => now - at > TimeSpan.FromHours(1));
    }
}
