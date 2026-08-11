namespace NoSilence.Tv;

internal enum WakeReason
{
    /// <summary>Nothing happened.</summary>
    None,

    /// <summary>The wall clock jumped, so the process was frozen: a real suspend or hibernate.</summary>
    ClockJumped,

    /// <summary>The output endpoint came back after a long absence: the display woke.</summary>
    OutputReturned,

    /// <summary>Input arrived after a long silence: somebody sat down at the machine.</summary>
    UserReturned,
}

/// <summary>
/// Notices that the machine has come back, without asking Windows to tell us.
/// </summary>
/// <remarks>
/// Written because the obvious mechanism does not work here. <c>PlaybackEngine</c> has
/// subscribed to <c>SystemEvents.PowerModeChanged</c> since the rewrite and its
/// "Resumed from sleep" line has <b>never once appeared</b> in seven days of logs, across
/// nights where the machine demonstrably went away and came back. The System event log explains
/// why: there are no <c>Kernel-Power</c> 42/107 suspend/resume pairs at all, only
/// <c>Kernel-Power 59 — "The system is entering Away Mode"</c>. In Away Mode the machine never
/// suspends; it keeps running with the display and audio switched off, so there is no resume to
/// be notified of and nothing for an ETW power-event listener to hear either.
/// <para>
/// So both signals here are polled, derived from state the tick already has, and independent of
/// any notification arriving:
/// </para>
/// <list type="number">
/// <item><b>The clock jumped.</b> The engine ticks continuously, so a gap far larger than the
/// tick interval means the process was frozen — a genuine S3 or hibernate. Cannot be missed and
/// has no false positives worth worrying about.</item>
/// <item><b>The output endpoint returned after a long absence.</b> Catches the case where the
/// television, and with it the HDMI endpoint, has been gone for hours.</item>
/// <item><b>Input arrived after a long silence.</b> The only one of the three that survives Away
/// Mode with the set switched off, which the logs say is what actually happens here: entering
/// Away Mode at 20:48:23 the endpoint went and came back within six seconds, so neither the
/// clock nor the endpoint has an edge to offer the next morning. Somebody touching the keyboard
/// after twelve hours does.</item>
/// </list>
/// <para>
/// Neither says anything about the <em>television</em> — the endpoint can come back with the set
/// still fast asleep, which is exactly the case that needs waking. That question is settled by
/// asking the set, in <c>TvService</c>.
/// </para>
/// </remarks>
internal sealed class WakeWatch
{
    /// <summary>
    /// How large a gap between observations means the process was frozen rather than busy.
    /// </summary>
    /// <remarks>
    /// Ninety seconds against a five-second cadence. Generous on purpose: a machine under heavy
    /// load, or a debugger break, can stall a tick for many seconds, and treating that as a wake
    /// would hand the television a power command for no reason.
    /// </remarks>
    public int ClockGapMs { get; init; } = 90000;

    /// <summary>
    /// How long the endpoint must have been gone for its return to count as the display waking.
    /// </summary>
    /// <remarks>
    /// Five minutes, which comfortably excludes the endpoint flap that happens every time the
    /// set changes state — three of those in one morning's log, each a second or two long.
    /// </remarks>
    public int EndpointAwayMs { get; init; } = 300000;

    /// <summary>How long the machine must have had no input for the next input to mean "back".</summary>
    /// <remarks>
    /// Fifteen minutes. Long enough that a coffee, a phone call or a long read is not a wake,
    /// short enough to catch an ordinary morning. Note what it costs when it is wrong: the
    /// television is asked its power state, and told to turn on only if it is off and there is
    /// music waiting — so the failure mode is "the room came on when I sat down", which is close
    /// to the point of the feature anyway.
    /// </remarks>
    public int IdleAwayMs { get; init; } = 900000;

    /// <summary>How recent the input has to be to count as the moment of return.</summary>
    public int IdleBackMs { get; init; } = 30000;

    private DateTimeOffset? _lastSeen;
    private DateTimeOffset? _endpointMissingSince;
    private TimeSpan? _lastIdle;

    /// <summary>
    /// Call once per evaluation. Returns non-<see cref="WakeReason.None"/> at most once per
    /// event.
    /// </summary>
    public WakeReason Observe(DateTimeOffset now, bool endpointPresent, TimeSpan userIdle)
    {
        DateTimeOffset? previous = _lastSeen;
        TimeSpan? previousIdle = _lastIdle;

        _lastSeen = now;
        _lastIdle = userIdle;

        bool clockJumped = previous is { } last && (now - last).TotalMilliseconds > ClockGapMs;

        // Both of the other two have to run every time even when the clock has already
        // decided, or their state is left describing a night that is already over.
        WakeReason endpoint = ObserveEndpoint(now, endpointPresent);

        bool userReturned = previousIdle is { } was
            && was.TotalMilliseconds > IdleAwayMs
            && userIdle.TotalMilliseconds < IdleBackMs;

        // The very first observation establishes the baseline and reports nothing: at launch
        // the startup window is already open, and firing here would only reopen it.
        if (previous is null)
        {
            return WakeReason.None;
        }

        // A real suspend satisfies all three at once. One wake, not three — the second and
        // third would land inside the first one's cooldown and be dropped with a warning.
        if (clockJumped)
        {
            return WakeReason.ClockJumped;
        }

        return endpoint != WakeReason.None ? endpoint
            : userReturned ? WakeReason.UserReturned
            : WakeReason.None;
    }

    private WakeReason ObserveEndpoint(DateTimeOffset now, bool endpointPresent)
    {
        if (!endpointPresent)
        {
            _endpointMissingSince ??= now;
            return WakeReason.None;
        }

        bool wasAwayLong = _endpointMissingSince is { } since
            && (now - since).TotalMilliseconds > EndpointAwayMs;

        _endpointMissingSince = null;
        return wasAwayLong ? WakeReason.OutputReturned : WakeReason.None;
    }
}
