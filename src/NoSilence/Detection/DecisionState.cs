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

    /// <summary>True when <paramref name="exeName"/> is the application currently in a call.</summary>
    public bool IsCallExe(string exeName) =>
        CallExe is { } exe && string.Equals(exe, exeName, StringComparison.OrdinalIgnoreCase);

    public void BeginCall(string sessionInstanceId, string app, string exeName)
    {
        CallSessionId = sessionInstanceId;
        CallApp = app;
        CallExe = exeName;
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
        LastTriggerWasCall = false;
        EndCall();
    }
}
