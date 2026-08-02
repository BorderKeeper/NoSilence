namespace NoSilence.Detection;

/// <summary>
/// The engine's memory between ticks. Plain mutable data, owned by the caller and passed in,
/// so <see cref="DecisionEngine.Evaluate"/> itself stays a pure function of
/// (snapshot, config, state).
/// </summary>
internal sealed class DecisionState
{
    public SessionTracker Tracker { get; } = new();

    public DecisionPhase Phase { get; set; } = DecisionPhase.Playing;

    /// <summary>When the current phase began. For display only.</summary>
    public DateTimeOffset PhaseEnteredAt { get; set; }

    /// <summary>
    /// When silence began, across the whole Ducked/Releasing/Overridden group.
    /// </summary>
    /// <remarks>
    /// Tracked separately from <see cref="PhaseEnteredAt"/> on purpose. Ducked and Releasing
    /// are two phases but one continuous stretch of silence; measuring the grace period from
    /// the phase timestamp would re-arm it on every tick spent releasing, and the music would
    /// never come back.
    /// </remarks>
    public DateTimeOffset? SilenceSince { get; set; }

    /// <summary>The last moment anything counted as noise. The release window measures from here.</summary>
    public DateTimeOffset? LastTriggerAt { get; set; }

    /// <summary>Transitions in the last hour, used to spot flapping without staring at it.</summary>
    public int TransitionsThisHour { get; set; }

    public DateTimeOffset TransitionWindowStartedAt { get; set; }

    public void Reset()
    {
        Tracker.Clear();
        Phase = DecisionPhase.Playing;
        LastTriggerAt = null;
        SilenceSince = null;
        TransitionsThisHour = 0;
    }
}
