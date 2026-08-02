namespace NoSilence.Detection;

/// <summary>
/// Every number the heuristic depends on, in one place.
/// </summary>
/// <remarks>
/// These defaults are a starting point, not a truth — which is exactly why
/// <c>--diagnose</c> records snapshots and <c>--replay</c> re-runs them. Change a value,
/// replay a real gaming session, and see where the decision flips, without relaunching
/// the game.
/// </remarks>
internal sealed class DetectionConfig
{
    /// <summary>
    /// How often sessions are sampled. 4 Hz is plenty; 100 ms buys nothing and costs power.
    /// </summary>
    public int PollIntervalMs { get; set; } = 250;

    /// <summary>
    /// Level above which a session counts as making sound.
    /// </summary>
    /// <remarks>
    /// -50 dBFS. Higher (-35) misses quiet dialogue and a game with the master at 10%,
    /// which is the failure that matters most. Lower (-70) picks up browser tabs holding an
    /// idle-but-open audio context and conferencing apps keeping a silent stream alive —
    /// that was v1's disease, at an effective -80.
    /// </remarks>
    public double ThresholdDb { get; set; } = -50d;

    /// <summary>
    /// Fraction of samples in the trailing window that must be above threshold.
    /// </summary>
    /// <remarks>
    /// Speech and music are bursty even measured as peaks over 250 ms, so a single-sample
    /// test flaps. Requiring a duty cycle turns "did it peak" into "is it consistently
    /// producing signal".
    /// </remarks>
    public double AttackRatio { get; set; } = 0.7d;

    /// <summary>How long a normal source must be consistently noisy before it counts.</summary>
    public int MinDurationMs { get; set; } = 1200;

    /// <summary>The longer requirement applied to Tolerant sources such as chat clients.</summary>
    public int TolerantMinDurationMs { get; set; } = 4000;

    /// <summary>
    /// Continuous quiet required before the music comes back.
    /// </summary>
    /// <remarks>
    /// 20 seconds, against v1's zero. Long enough to survive an ad break, a scene
    /// transition, or pausing a video to read something — all of which would otherwise
    /// bring music up over the top of what you are watching.
    /// </remarks>
    public int ReleaseMs { get; set; } = 20000;

    /// <summary>Fade down. Fast enough to feel immediate, slow enough not to click.</summary>
    public int DuckFadeOutMs { get; set; } = 400;

    /// <summary>Fade back up. Slow, so the return is unobtrusive.</summary>
    public int DuckFadeInMs { get; set; } = 3000;

    /// <summary>Minimum time to stay ducked once ducked, so it cannot bounce straight back.</summary>
    public int HardDuckGraceMs { get; set; } = 500;

    /// <summary>Gain while ducked. Zero is silence; some people prefer a low murmur.</summary>
    public float DuckedGain { get; set; }

    /// <summary>Ignore everything while the output endpoint is muted or at zero — you would hear nothing anyway.</summary>
    public bool IgnoreWhenEndpointMuted { get; set; } = true;

    /// <summary>Ignore sessions the user has muted in the Volume Mixer.</summary>
    public bool IgnoreMutedSessions { get; set; } = true;

    /// <summary>
    /// Divide out an application's own volume slider before measuring.
    /// </summary>
    /// <remarks>
    /// Off by default. Session meters read after the app's slider, so an app pulled to 5%
    /// measures ~26 dB lower — but an app you deliberately quietened arguably <em>should</em>
    /// be ignorable, and the division amplifies noise near zero.
    /// </remarks>
    public bool CompensateSessionVolume { get; set; }

    // ---- optional signals ------------------------------------------------

    /// <summary>Treat an active microphone as a reason for silence. You are probably on a call.</summary>
    public bool MicrophoneSignal { get; set; } = true;

    public double MicThresholdDb { get; set; } = -45d;

    public int MicMinDurationMs { get; set; } = 3000;

    /// <summary>
    /// Count a capture session as noise merely for being active, without measuring level.
    /// </summary>
    /// <remarks>
    /// Off by default, and this is the setting most likely to cause false positives:
    /// Voicemeeter, OBS, NVIDIA Broadcast and several headset utilities hold a capture
    /// session open permanently.
    /// </remarks>
    public bool TreatActiveCaptureAsNoise { get; set; }

    /// <summary>Capture sources that never count. Pre-populated but certainly incomplete.</summary>
    public List<string> MicExclusions { get; set; } =
    [
        "obs64.exe",
        "obs32.exe",
        "voicemeeter*.exe",
        "NVIDIA Broadcast.exe",
        "SteelSeriesSonar*.exe",
        "audiodg.exe",
        "NoSilence.exe",
    ];

    /// <summary>
    /// Treat a full-screen exclusive application as a reason for silence.
    /// </summary>
    /// <remarks>
    /// Supplementary only. Borderless-windowed games — which is most modern games — report
    /// as ordinary windows, so this catches true exclusive full screen and presentation mode
    /// and nothing else. The session heuristic carries the load.
    /// </remarks>
    public bool FullscreenSignal { get; set; } = true;

    /// <summary>Treat Focus Assist as a reason for silence. Off: some people enable it *to* focus with music.</summary>
    public bool FocusAssistSignal { get; set; }

    /// <summary>Go quiet after the machine has been idle this long. Zero disables it.</summary>
    public int SilenceWhenIdleMinutes { get; set; }

    /// <summary>Go quiet while the workstation is locked.</summary>
    public bool SilenceWhenLocked { get; set; }

    /// <summary>Per-application rules, first match wins.</summary>
    public List<ProcessRule> Rules { get; set; } = [.. ProcessRule.BuiltInRules];
}
