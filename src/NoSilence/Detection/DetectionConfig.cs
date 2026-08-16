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

    /// <summary>
    /// How long a normal source must be consistently noisy before it counts.
    /// </summary>
    /// <remarks>
    /// Two seconds. Started at 1200 ms and was raised after a Windows console bell — about a
    /// second long — was observed ducking the music in real use. The costs here are heavily
    /// asymmetric: ducking 0.8 s later than strictly necessary is barely perceptible, while a
    /// false duck buys 20 seconds of silence over a notification chime.
    /// </remarks>
    public int MinDurationMs { get; set; } = 2000;

    /// <summary>The longer requirement applied to Tolerant sources such as chat clients.</summary>
    public int TolerantMinDurationMs { get; set; } = 4000;

    /// <summary>
    /// Continuous quiet required before the music comes back.
    /// </summary>
    /// <remarks>
    /// Five seconds, against v1's zero. Started at twenty on the reasoning that it should
    /// survive an ad break or pausing a video to read something, but twenty seconds of dead
    /// air is very noticeable in daily use, and five turned out to feel much better. The
    /// trade-off is real and worth knowing: a mid-video pause longer than five seconds now
    /// brings the music back over the top of what you are watching. Raise it if that bites.
    /// </remarks>
    public int ReleaseMs { get; set; } = 5000;

    /// <summary>
    /// Continuous quiet required before the music comes back <em>after a call</em>.
    /// </summary>
    /// <remarks>
    /// Fifteen seconds, against five for everything else, and the reason is that a call is
    /// not like other noise. It is one continuous context that the level meter samples as a
    /// string of unrelated bursts, so the ordinary release fires in every pause for breath:
    /// two days of real use produced 352 play/silence flips in a day, and at 11:04 the music
    /// came back up for 751 ms in the middle of a meeting.
    /// <para>
    /// Most of the work is done by holding silence while the capture session stays open — see
    /// <see cref="CaptureMode.Call"/> — so this only covers the tail after the microphone
    /// closes, where a conferencing client can briefly reopen it between meetings.
    /// </para>
    /// </remarks>
    public int CallReleaseMs { get; set; } = 15000;

    /// <summary>
    /// How long a call may produce nothing at all — no microphone signal, no audio from the
    /// same application — before it is treated as over.
    /// </summary>
    /// <remarks>
    /// The safety net on the call hold, and the reason it cannot strand the music. Some
    /// clients keep the capture session open after a meeting ends; without this, "hold while
    /// the microphone is open" would mean "hold until the application exits".
    /// <para>
    /// Thirty minutes, against the two it shipped with, and the change is the point rather
    /// than a tweak. At two minutes this was not a safety net, it was the everyday mechanism:
    /// a log of 14 August shows one Zoom meeting broken into seven separate calls, each ending
    /// after exactly two minutes of nobody speaking and re-arming on the next word, with the
    /// music surfacing for up to three minutes in between. Quiet stretches inside a meeting
    /// are ordinary — reading a screen share, waiting for somebody to join, sitting muted.
    /// The signal that a meeting is over is the client closing its microphone, which it does
    /// promptly; this only covers the one that does not.
    /// </para>
    /// </remarks>
    public int CallIdleTimeoutMs { get; set; } = 1800000;

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

    /// <summary>
    /// Capture sources that never count, on top of anything the rules already ignore.
    /// </summary>
    /// <remarks>
    /// The always-on tools are the well-known trap: OBS, Voicemeeter, NVIDIA Broadcast and
    /// several headset utilities hold a capture session open permanently. Windows Settings
    /// is here because its sound page opens one just to draw a level meter, which was
    /// observed silencing the music within seconds of a real launch.
    /// </remarks>
    public List<string> MicExclusions { get; set; } =
    [
        "obs64.exe",
        "obs32.exe",
        "voicemeeter*.exe",
        "NVIDIA Broadcast.exe",
        "SteelSeriesSonar*.exe",
        "audiodg.exe",
        "NoSilence.exe",
        "SystemSettings.exe",
        "ApplicationFrameHost.exe",
        "ShellExperienceHost.exe",
        "SearchHost.exe",
        "explorer.exe",
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
