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

    /// <summary>
    /// The capture session of the call currently holding silence, or null when not in one.
    /// </summary>
    /// <remarks>
    /// Held by session instance rather than by name so that a client which closes and reopens
    /// its microphone — between two meetings, or when the input device is switched — starts a
    /// genuinely new call rather than silently inheriting the old one's hold.
    /// </remarks>
    public string? CallSessionId { get; private set; }

    /// <summary>The application in that call, for the tray and the log.</summary>
    public string? CallApp { get; private set; }

    /// <summary>
    /// The last moment a call-capable application produced anything — microphone signal, or
    /// audio of its own. Bounds the hold, so an open-but-dead capture session cannot strand
    /// the music.
    /// </summary>
    public DateTimeOffset? CallSignalAt { get; private set; }

    public void NoteCallSignal(DateTimeOffset at) => CallSignalAt = at;

    /// <summary>
    /// Whether the most recent duck belonged to a call, which decides the release window.
    /// </summary>
    /// <remarks>
    /// Recorded at the moment of ducking rather than read live, because by the time the
    /// release is being evaluated the call is over by definition — that is what started the
    /// release timer.
    /// </remarks>
    public bool LastTriggerWasCall { get; set; }

    /// <summary>Executable behind the current call, so its own audio can keep the call alive.</summary>
    public string? CallExe { get; private set; }

    /// <summary>
    /// A capture session the idle timeout has already given up on, so it cannot start a call
    /// again until something is actually heard through it.
    /// </summary>
    /// <remarks>
    /// The counterweight to arming on the open microphone. Without the latch, a client that
    /// keeps an active capture session open after its meeting has ended would be timed out and
    /// then re-armed by the very next tick, for ever — the timeout would do nothing at all
    /// except log a line four times a second.
    /// </remarks>
    public string? ExhaustedCallSessionId { get; private set; }

    /// <summary>True when the flap warning has already been raised for the current hour.</summary>
    public bool FlapReported { get; private set; }

    /// <summary>
    /// True exactly once per hour, on the transition that takes the count past
    /// <paramref name="threshold"/>.
    /// </summary>
    /// <remarks>
    /// A latch, and it lives here rather than at the call site because the obvious version of
    /// it is wrong in a way that survived real use. Testing <c>TransitionsThisHour == 20</c>
    /// where the decision is logged reads as "once an hour" and is not: Ducked→Releasing is a
    /// second logged change at the same count, so every warning arrived twice. The eleven
    /// warnings in the log of 15 August are five pairs and a single, seconds apart, and each
    /// pair was two balloons.
    /// </remarks>
    public bool ShouldReportFlapping(int threshold)
    {
        if (TransitionsThisHour < threshold || FlapReported)
        {
            return false;
        }

        FlapReported = true;
        return true;
    }

    /// <summary>Re-arms the warning. Called when the transition window rolls over.</summary>
    public void ClearFlapReport() => FlapReported = false;

    /// <summary>True when <paramref name="exeName"/> is the application currently in a call.</summary>
    public bool IsCallExe(string exeName) =>
        CallExe is { } exe && string.Equals(exe, exeName, StringComparison.OrdinalIgnoreCase);

    public bool IsCallExhausted(string sessionInstanceId) =>
        string.Equals(ExhaustedCallSessionId, sessionInstanceId, StringComparison.Ordinal);

    public void NoteCallExhausted(string sessionInstanceId) => ExhaustedCallSessionId = sessionInstanceId;

    /// <summary>
    /// Forgets that <paramref name="sessionInstanceId"/> was given up on, so it can start a
    /// call again. Scoped to the session, so signal from one client cannot revive another's.
    /// </summary>
    public void ReviveCall(string sessionInstanceId)
    {
        if (IsCallExhausted(sessionInstanceId))
        {
            ExhaustedCallSessionId = null;
        }
    }

    public void ClearExhaustedCall() => ExhaustedCallSessionId = null;

    /// <summary>
    /// Starts a call, or does nothing if this session is already the one in progress.
    /// </summary>
    /// <remarks>
    /// Idempotent because the caller now runs it on every tick the microphone is open, not
    /// only on the tick somebody spoke. Re-seeding <see cref="CallSignalAt"/> each time would
    /// keep the idle timeout permanently in the future and the safety net would never fire.
    /// </remarks>
    public void BeginCall(string sessionInstanceId, string app, string exeName, DateTimeOffset at)
    {
        if (string.Equals(CallSessionId, sessionInstanceId, StringComparison.Ordinal))
        {
            return;
        }

        CallSessionId = sessionInstanceId;
        CallApp = app;
        CallExe = exeName;

        // Seeded, because a call arms on the microphone being open rather than on your voice.
        // Left null, the hold would expire on the tick it began.
        CallSignalAt = at;
    }

    public void EndCall()
    {
        CallSessionId = null;
        CallApp = null;
        CallExe = null;
        CallSignalAt = null;
    }

    public void Reset()
    {
        Tracker.Clear();
        Phase = DecisionPhase.Playing;
        LastTriggerAt = null;
        SilenceSince = null;
        TransitionsThisHour = 0;
        ClearFlapReport();
        LastTriggerWasCall = false;
        ClearExhaustedCall();
        EndCall();
    }
}
