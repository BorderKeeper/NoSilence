namespace NoSilence.Detection;

/// <summary>
/// Local mirror of <c>SHQueryUserNotificationState</c>, so the detection zone never has to
/// reference interop.
/// </summary>
internal enum ShellActivity
{
    Unknown = 0,
    ScreenSaverOrLocked = 1,
    Busy = 2,
    FullScreenD3D = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    AppFullScreen = 7,
}

internal enum OperatingMode
{
    /// <summary>The heuristic decides.</summary>
    Auto,

    /// <summary>Play regardless of what else is making noise.</summary>
    AlwaysPlay,

    /// <summary>Stay silent regardless.</summary>
    AlwaysSilent,
}

/// <summary>
/// The user's manual override. Part of the snapshot rather than a special case in the
/// plumbing, so overrides flow through the same pure engine as everything else — including
/// snooze expiry, which is evaluated from the snapshot's own timestamp and therefore needs
/// no timer to leak.
/// </summary>
internal sealed record OverrideState(
    OperatingMode Mode = OperatingMode.Auto,
    DateTimeOffset? SnoozeUntil = null,
    bool PlayThroughCall = false)
{
    public static OverrideState Auto { get; } = new();

    public bool IsSnoozed(DateTimeOffset now) => SnoozeUntil is { } until && until > now;
}

/// <summary>
/// Everything the decision engine is allowed to look at, at one instant.
/// </summary>
/// <remarks>
/// Fully serialisable by design: <c>--diagnose --jsonl</c> writes one of these per tick, and
/// <c>--replay</c> feeds them back through the engine. That loop is the only way to answer
/// "is -50 dBFS the right threshold?", because no conventional test can.
/// </remarks>
internal sealed record DetectionSnapshot(
    DateTimeOffset At,
    IReadOnlyList<SessionObservation> Render,
    IReadOnlyList<SessionObservation> Capture,
    bool OutputEndpointPresent,
    bool DefaultEndpointMuted,
    float DefaultEndpointVolume,
    ShellActivity Shell,
    TimeSpan UserIdle,
    bool WorkstationLocked,
    OverrideState Override)
{
    public static DetectionSnapshot Empty(DateTimeOffset at) => new(
        at, [], [], true, false, 1f, ShellActivity.AcceptsNotifications, TimeSpan.Zero, false, OverrideState.Auto);
}
