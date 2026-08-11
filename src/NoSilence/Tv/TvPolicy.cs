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
    DisplayCapabilities Capabilities,
    DateTimeOffset StartedAt,
    DisplayPowerState? ReportedPower = null);

internal sealed class TvPolicyConfig
{
    public bool WakeEnabled { get; set; }

    /// <summary>
    /// Turn the television on shortly after NoSilence starts, without waiting out
    /// <see cref="RequireWantsToPlayForMs"/> and without <see cref="WakeEnabled"/>.
    /// </summary>
    /// <remarks>
    /// A launch is not the "brief gap between videos" the two-minute rule exists to survive,
    /// so measuring one with the other only delays the obvious: the machine has just come up,
    /// there is music to play and the set is off.
    /// <para>
    /// Independent of <see cref="WakeEnabled"/> on purpose. Continuous automatic waking is the
    /// thing people leave off because it can act at any moment; a single attempt bounded to
    /// the first few minutes after launch is predictable, and it is what "turn the TV on when
    /// I sit down" actually asks for. Every other guard still applies — the user veto included,
    /// so switching the set off by hand and restarting NoSilence does not turn it back on.
    /// </para>
    /// </remarks>
    public bool WakeAtStartup { get; set; } = true;

    /// <summary>How long after launch still counts as starting up.</summary>
    /// <remarks>
    /// Five minutes, which is also the cooldown, so the startup path gets one attempt rather
    /// than a series of them.
    /// </remarks>
    public int StartupWindowMs { get; set; } = 300000;

    /// <summary>The shortened <see cref="RequireWantsToPlayForMs"/> used inside that window.</summary>
    /// <remarks>
    /// Not zero. The output endpoint takes a moment to open, so for the first seconds after
    /// launch a television that is already on still looks off — and if the machine came up
    /// into a call or a game, the engine stops wanting to play and no wake should happen at
    /// all.
    /// </remarks>
    public int StartupWakeAfterMs { get; set; } = 15000;

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
        if (!input.Capabilities.HasFlag(DisplayCapabilities.Wake))
        {
            return false;
        }

        bool atStartup = config.WakeAtStartup
            && (input.Now - input.StartedAt).TotalMilliseconds < config.StartupWindowMs;

        if (!config.WakeEnabled && !atStartup)
        {
            return false;
        }

        // The audio endpoint is the free "is it on?" sensor, and normally the only one: never
        // send a power command that disagrees with it. But it is not reliable — a standby
        // entered with KEY_POWEROFF leaves the HDMI link asserted, so Windows keeps the
        // endpoint Active while the screen is dark, and this machine's television does exactly
        // that. Where the set has been asked directly, its own answer wins.
        bool believedOn = input.ReportedPower switch
        {
            DisplayPowerState.On => true,
            DisplayPowerState.Standby or DisplayPowerState.Off => false,
            _ => input.OutputEndpointPresent,
        };

        if (believedOn || !input.LibraryHasTracks)
        {
            return false;
        }

        if (input.Snoozed || input.Mode == OperatingMode.AlwaysSilent)
        {
            return false;
        }

        int requiredMs = atStartup
            ? Math.Min(config.RequireWantsToPlayForMs, config.StartupWakeAfterMs)
            : config.RequireWantsToPlayForMs;

        if (state.WantsToPlaySince is not { } since || (input.Now - since).TotalMilliseconds < requiredMs)
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

    /// <summary>
    /// Drops the two timers that only mean anything within one run of the app.
    /// </summary>
    /// <remarks>
    /// <see cref="TvPolicyState.WantsToPlaySince"/> and <see cref="TvPolicyState.IdleSince"/>
    /// are observations of the current session that happen to be serialised alongside the
    /// bookkeeping that genuinely has to survive a restart. Left in place, a timestamp from
    /// yesterday satisfies the wait-before-waking rule on the very first tick after launch,
    /// which is both surprising and impossible to reason about.
    /// </remarks>
    public static void BeginSession(TvPolicyState state)
    {
        state.WantsToPlaySince = null;
        state.IdleSince = null;
    }

    private static void PruneCommandHistory(DateTimeOffset now, TvPolicyState state)
    {
        if (state.RecentPowerCommands.Count == 0)
        {
            return;
        }

        state.RecentPowerCommands.RemoveAll(at => now - at > TimeSpan.FromHours(1));
    }
}
