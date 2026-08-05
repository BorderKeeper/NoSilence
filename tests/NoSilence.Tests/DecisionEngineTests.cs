using NoSilence.Detection;

namespace NoSilence.Tests;

/// <summary>
/// The engine takes its time from the snapshot, never from a clock, so these tests advance
/// a fake clock tick by tick and assert on exact millisecond boundaries.
/// </summary>
public class DecisionEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static DetectionConfig Config() => new()
    {
        PollIntervalMs = 250,
        ThresholdDb = -50,
        AttackRatio = 0.7,
        MinDurationMs = 1200,
        ReleaseMs = 20000,
        HardDuckGraceMs = 500,
        MicrophoneSignal = false,
        FullscreenSignal = false,
    };

    private static SessionObservation Session(
        string exe,
        double dbfs,
        uint pid = 4242,
        bool ours = false,
        bool systemSounds = false,
        bool muted = false,
        float volume = 1f) => new(
            SessionInstanceId: $"session-{exe}-{pid}",
            EndpointId: "endpoint-1",
            EndpointName: "Headphones",
            ProcessId: pid,
            ExeName: exe,
            DisplayName: null,
            IsSystemSounds: systemSounds,
            IsOurProcess: ours,
            State: SessionActivity.Active,
            Peak: (float)PeakMath.FromDbfs(dbfs),
            SessionVolume: volume,
            SessionMuted: muted);

    /// <summary>Drives the engine forward, returning the outcome of the final tick.</summary>
    private static DecisionOutcome RunFor(
        DecisionState state,
        DetectionConfig config,
        ref DateTimeOffset clock,
        int milliseconds,
        params SessionObservation[] sessions)
    {
        DecisionOutcome outcome = null!;
        int ticks = Math.Max(1, milliseconds / config.PollIntervalMs);

        for (int i = 0; i < ticks; i++)
        {
            var snapshot = DetectionSnapshot.Empty(clock) with { Render = sessions };
            outcome = DecisionEngine.Evaluate(snapshot, config, state);
            clock = clock.AddMilliseconds(config.PollIntervalMs);
        }

        return outcome;
    }

    [Fact]
    public void NothingPlaying_MeansPlay()
    {
        var state = new DecisionState();
        DecisionOutcome outcome = DecisionEngine.Evaluate(DetectionSnapshot.Empty(Start), Config(), state);

        Assert.False(outcome.WantsSilence);
        Assert.Equal(1f, outcome.TargetGain);
    }

    /// <summary>
    /// The core promise. v1 blocked for three seconds and then re-read once; this must reach
    /// the decision purely from the trailing window, without ever blocking.
    /// </summary>
    [Fact]
    public void SustainedAudio_DucksAfterTheConfiguredDelay()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        SessionObservation loud = Session("chrome.exe", -20);

        // 1000 ms is under the 1200 ms sustain requirement.
        Assert.False(RunFor(state, config, ref clock, 1000, loud).WantsSilence);

        // Carrying on past it must duck.
        Assert.True(RunFor(state, config, ref clock, 1500, loud).WantsSilence);
    }

    /// <summary>
    /// A Discord ping is about a second. This is the single most important false positive to
    /// avoid, because it is the one that happens all day.
    /// </summary>
    [Fact]
    public void ShortBlip_NeverDucks()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        // One second of noise, then quiet. Well under the 1200 ms requirement.
        Assert.False(RunFor(state, config, ref clock, 1000, Session("chrome.exe", -20)).WantsSilence);
        Assert.False(RunFor(state, config, ref clock, 3000, Session("chrome.exe", -100)).WantsSilence);
    }

    [Fact]
    public void QuietAudioBelowThreshold_DoesNotDuck()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        // -60 dBFS is below the -50 threshold: an idle-but-open audio context.
        Assert.False(RunFor(state, config, ref clock, 10000, Session("chrome.exe", -60)).WantsSilence);
    }

    /// <summary>
    /// v1's threshold of 0.0001f is -80 dBFS, so a near-silent stream pinned it permanently.
    /// </summary>
    [Fact]
    public void V1Threshold_WouldHaveTriggered_ButOursDoesNot()
    {
        Assert.True(PeakMath.ToDbfs(0.0001f) < -79);

        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        Assert.False(RunFor(state, config, ref clock, 10000, Session("chrome.exe", -75)).WantsSilence);
    }

    [Fact]
    public void ReleaseRequiresTheFullQuietWindow()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        RunFor(state, config, ref clock, 3000, Session("chrome.exe", -20));

        // Ten seconds of quiet is not enough; the release window is twenty.
        Assert.True(RunFor(state, config, ref clock, 10000, Session("chrome.exe", -100)).WantsSilence);

        // Past twenty, the music comes back.
        Assert.False(RunFor(state, config, ref clock, 11000, Session("chrome.exe", -100)).WantsSilence);
    }

    /// <summary>Pausing a video to read something must not bring music up over the top of it.</summary>
    [Fact]
    public void PausingAVideoBriefly_DoesNotResumeMusic()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        RunFor(state, config, ref clock, 3000, Session("chrome.exe", -20));
        Assert.True(RunFor(state, config, ref clock, 15000, Session("chrome.exe", -100)).WantsSilence);
        Assert.True(RunFor(state, config, ref clock, 3000, Session("chrome.exe", -20)).WantsSilence);
    }

    [Fact]
    public void ASingleNoisyTickResetsTheReleaseCountdown()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        RunFor(state, config, ref clock, 3000, Session("chrome.exe", -20));
        RunFor(state, config, ref clock, 18000, Session("chrome.exe", -100));

        // Noise again just before the window closes, then another 15 s of quiet: still silent.
        RunFor(state, config, ref clock, 2000, Session("chrome.exe", -20));
        Assert.True(RunFor(state, config, ref clock, 15000, Session("chrome.exe", -100)).WantsSilence);
    }

    /// <summary>
    /// The check that removes v1's rule that the music device had to differ from the watched
    /// device. Without it the app hears itself and oscillates forever.
    /// </summary>
    [Fact]
    public void OurOwnPlayback_NeverCountsHoweverLoud()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        DecisionOutcome outcome = RunFor(state, config, ref clock, 30000, Session("NoSilence.exe", -3, pid: 1234, ours: true));

        Assert.False(outcome.WantsSilence);
        Assert.Contains(outcome.Contributions, c => c is { Counts: false, Rule: "self" });
    }

    [Fact]
    public void SystemSounds_NeverCount()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        Assert.False(RunFor(state, config, ref clock, 30000, Session("(system sounds)", -10, pid: 0, systemSounds: true)).WantsSilence);
    }

    [Fact]
    public void MutedSession_DoesNotCount()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        Assert.False(RunFor(state, config, ref clock, 10000, Session("chrome.exe", -10, muted: true)).WantsSilence);
    }

    [Fact]
    public void MutedOutputEndpoint_MeansNothingCounts()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;
        DecisionOutcome outcome = null!;

        for (int i = 0; i < 40; i++)
        {
            var snapshot = DetectionSnapshot.Empty(clock) with
            {
                Render = [Session("chrome.exe", -10)],
                DefaultEndpointMuted = true,
            };

            outcome = DecisionEngine.Evaluate(snapshot, config, state);
            clock = clock.AddMilliseconds(config.PollIntervalMs);
        }

        Assert.False(outcome.WantsSilence);
    }

    [Fact]
    public void AlwaysTriggerRule_DucksImmediately()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        // vlc.exe is AlwaysTrigger out of the box: you started it deliberately.
        Assert.True(RunFor(state, config, ref clock, 250, Session("vlc.exe", -20)).WantsSilence);
    }

    /// <summary>A Discord ping must not duck, but a Discord call must.</summary>
    [Fact]
    public void TolerantRule_IgnoresPingsButNotCalls()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        Assert.False(RunFor(state, config, ref clock, 2000, Session("discord.exe", -20)).WantsSilence);

        state = new DecisionState();
        clock = Start;
        Assert.True(RunFor(state, config, ref clock, 6000, Session("discord.exe", -20)).WantsSilence);
    }

    [Fact]
    public void AlwaysSilentMode_OverridesEverything()
    {
        var config = Config();
        var state = new DecisionState();
        var snapshot = DetectionSnapshot.Empty(Start) with { Override = new OverrideState(OperatingMode.AlwaysSilent) };

        DecisionOutcome outcome = DecisionEngine.Evaluate(snapshot, config, state);

        Assert.True(outcome.WantsSilence);
        Assert.Equal(DecisionPhase.Overridden, outcome.Phase);
    }

    [Fact]
    public void AlwaysPlayMode_OverridesEvenLoudAudio()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;
        DecisionOutcome outcome = null!;

        for (int i = 0; i < 40; i++)
        {
            var snapshot = DetectionSnapshot.Empty(clock) with
            {
                Render = [Session("chrome.exe", -5)],
                Override = new OverrideState(OperatingMode.AlwaysPlay),
            };

            outcome = DecisionEngine.Evaluate(snapshot, config, state);
            clock = clock.AddMilliseconds(config.PollIntervalMs);
        }

        Assert.False(outcome.WantsSilence);
    }

    /// <summary>Snooze expiry is derived from the snapshot's own clock, so no timer can leak.</summary>
    [Fact]
    public void Snooze_ExpiresOnItsOwn()
    {
        var config = Config();
        var state = new DecisionState();
        var snooze = new OverrideState(OperatingMode.Auto, Start.AddMinutes(15));

        var during = DetectionSnapshot.Empty(Start.AddMinutes(5)) with { Override = snooze };
        Assert.True(DecisionEngine.Evaluate(during, config, state).WantsSilence);

        var after = DetectionSnapshot.Empty(Start.AddMinutes(16)) with { Override = snooze };
        Assert.False(DecisionEngine.Evaluate(after, config, state).WantsSilence);
    }

    [Fact]
    public void IgnoredSessionsAreStillReported_SoTheHeuristicCanBeDebugged()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        DecisionOutcome outcome = RunFor(state, config, ref clock, 2000, Session("explorer.exe", -10));

        Assert.False(outcome.WantsSilence);
        Assert.Contains(outcome.Contributions, c => c.Source.Contains("explorer", StringComparison.OrdinalIgnoreCase) && !c.Counts);
    }

    [Fact]
    public void HardDuckGrace_PreventsAnImmediateBounceBack()
    {
        DetectionConfig config = Config();
        config.ReleaseMs = 0;      // release instantly, so only the grace period holds it
        config.HardDuckGraceMs = 2000;

        var state = new DecisionState();
        DateTimeOffset clock = Start;

        // vlc is AlwaysTrigger, so one tick ducks.
        RunFor(state, config, ref clock, 250, Session("vlc.exe", -20));

        // Still inside the grace period: must not bounce straight back.
        Assert.True(RunFor(state, config, ref clock, 500, Session("vlc.exe", -100)).WantsSilence);

        // Once the grace has passed and the release window is zero, it resumes.
        Assert.False(RunFor(state, config, ref clock, 2500, Session("vlc.exe", -100)).WantsSilence);
    }

    /// <summary>
    /// Regression: a trigger was reported at "-100.0 dBFS", because the duty-cycle window
    /// can be satisfied by the earlier part of the window while the newest sample is already
    /// silent. The level shown must be the one that caused the trigger.
    /// </summary>
    [Fact]
    public void TheReportedLevelIsThePeakThatTriggered_NotTheLatestSample()
    {
        var config = Config();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        // Browsers carry a 2500 ms rule, so this has to run past that to qualify at all.
        RunFor(state, config, ref clock, 3000, Session("chrome.exe", -12));

        // One silent tick: the window still qualifies, but the newest sample is silence.
        DecisionOutcome outcome = RunFor(state, config, ref clock, 250, Session("chrome.exe", -100));

        Assert.True(outcome.WantsSilence);
        Assert.DoesNotContain("-100", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("-12", outcome.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Windows console bell runs about a second. Observed ducking the music at the old
    /// 1200 ms default, so the shipped default has to sit above it.
    /// </summary>
    [Fact]
    public void DefaultSustain_IsLongEnoughToRejectASecondLongChime()
    {
        Assert.True(new DetectionConfig().MinDurationMs >= 2000);
    }

    [Fact]
    public void ConsoleHosts_NeverCount()
    {
        var config = Config();
        DateTimeOffset clock = Start;

        foreach (string shell in new[] { "powershell.exe", "pwsh.exe", "cmd.exe", "WindowsTerminal.exe", "conhost.exe" })
        {
            var state = new DecisionState();
            clock = Start;
            Assert.False(
                RunFor(state, config, ref clock, 30000, Session(shell, -10)).WantsSilence,
                $"{shell} should never silence the music");
        }
    }

    /// <summary>
    /// Regression from a real launch: Windows Settings holds a capture session whenever its
    /// sound page is open, purely to draw a level meter, and silenced the music three
    /// seconds after startup.
    /// </summary>
    [Fact]
    public void ShellApplicationsHoldingTheMicrophoneDoNotCount()
    {
        DetectionConfig config = Config();
        config.MicrophoneSignal = true;

        foreach (string shell in new[] { "SystemSettings.exe", "explorer.exe", "SearchHost.exe" })
        {
            var state = new DecisionState();
            DateTimeOffset clock = Start;
            DecisionOutcome outcome = null!;

            for (int i = 0; i < 40; i++)
            {
                var snapshot = DetectionSnapshot.Empty(clock) with
                {
                    Capture = [Session(shell, -18) with { EndpointName = "Microphone" }],
                };

                outcome = DecisionEngine.Evaluate(snapshot, config, state);
                clock = clock.AddMilliseconds(config.PollIntervalMs);
            }

            Assert.False(outcome.WantsSilence, $"{shell} on the microphone should not silence the music");
        }
    }

    /// <summary>A real call still has to work — this must not have disabled the signal.</summary>
    [Fact]
    public void ARealApplicationOnTheMicrophoneStillCounts()
    {
        DetectionConfig config = Config();
        config.MicrophoneSignal = true;

        var state = new DecisionState();
        DateTimeOffset clock = Start;
        DecisionOutcome outcome = null!;

        for (int i = 0; i < 40; i++)
        {
            var snapshot = DetectionSnapshot.Empty(clock) with
            {
                Capture = [Session("discord.exe", -18) with { EndpointName = "Microphone" }],
            };

            outcome = DecisionEngine.Evaluate(snapshot, config, state);
            clock = clock.AddMilliseconds(config.PollIntervalMs);
        }

        Assert.True(outcome.WantsSilence);
    }

    /// <summary>Drives the engine with one capture session, returning the final outcome.</summary>
    private static DecisionOutcome RunCapture(
        DecisionState state,
        DetectionConfig config,
        ref DateTimeOffset clock,
        int milliseconds,
        SessionObservation? microphone)
    {
        DecisionOutcome outcome = null!;
        int ticks = Math.Max(1, milliseconds / config.PollIntervalMs);

        for (int i = 0; i < ticks; i++)
        {
            var snapshot = DetectionSnapshot.Empty(clock) with
            {
                Capture = microphone is null ? [] : [microphone],
            };

            outcome = DecisionEngine.Evaluate(snapshot, config, state);
            clock = clock.AddMilliseconds(config.PollIntervalMs);
        }

        return outcome;
    }

    /// <summary>
    /// A capture session. The distinct process id matters only to keep its session instance
    /// id apart from the render one in <see cref="SessionTracker"/>; on a real machine the two
    /// are different sessions on different endpoints and never collide.
    /// </summary>
    private static SessionObservation Microphone(string exe, double dbfs, SessionActivity activity = SessionActivity.Active) =>
        Session(exe, dbfs, pid: 4243) with { EndpointName = "Microphone", State = activity };

    private static DetectionConfig CallConfig()
    {
        DetectionConfig config = Config();
        config.MicrophoneSignal = true;
        config.MicMinDurationMs = 3000;
        config.ReleaseMs = 5000;
        config.CallReleaseMs = 15000;
        return config;
    }

    /// <summary>
    /// The bug that two days of daily use produced: 352 play/silence flips in one day, almost
    /// all of them a single Zoom call. The level meter goes quiet every time you stop talking,
    /// so the ordinary release fired in every pause for breath.
    /// </summary>
    [Fact]
    public void ACallHoldsSilenceThroughEveryPause()
    {
        DetectionConfig config = CallConfig();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        // Someone speaks: this arms the call on exactly the old trigger condition.
        Assert.True(RunCapture(state, config, ref clock, 4000, Microphone("Zoom.exe", -18)).WantsSilence);

        // Now nobody speaks for a full minute, but Zoom keeps the microphone open. Under the
        // old 5 s release this produced a dozen transitions.
        DecisionOutcome outcome = RunCapture(state, config, ref clock, 60000, Microphone("Zoom.exe", -100));

        Assert.True(outcome.WantsSilence);
        Assert.Equal(DecisionPhase.Ducked, outcome.Phase);
        Assert.Equal("In a call — Zoom.exe", outcome.Reason);
    }

    /// <summary>One transition in, one out. Not twenty.</summary>
    [Fact]
    public void ACallProducesExactlyTwoTransitions()
    {
        DetectionConfig config = CallConfig();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        // Five minutes of alternating speech and silence — a conversation.
        for (int i = 0; i < 30; i++)
        {
            RunCapture(state, config, ref clock, 4000, Microphone("Zoom.exe", -18));
            RunCapture(state, config, ref clock, 6000, Microphone("Zoom.exe", -100));
        }

        // The meeting ends: Zoom closes the capture session.
        RunCapture(state, config, ref clock, 20000, null);

        Assert.Equal(2, state.TransitionsThisHour);
    }

    [Fact]
    public void ACallReleasesOnlyAfterTheMicrophoneCloses()
    {
        DetectionConfig config = CallConfig();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        RunCapture(state, config, ref clock, 4000, Microphone("Zoom.exe", -18));

        // Zoom drops the capture session. The ordinary 5 s release must not apply.
        Assert.True(RunCapture(state, config, ref clock, 10000, null).WantsSilence);

        // Past the 15 s call release, the music comes back.
        Assert.False(RunCapture(state, config, ref clock, 6000, null).WantsSilence);
    }

    /// <summary>
    /// The dangerous version of this feature is the one where a conferencing client sitting
    /// idle in the tray with an open microphone silences the music forever. Arming requires
    /// real signal, so an idle microphone never starts a call.
    /// </summary>
    [Fact]
    public void AnIdleMicrophoneNeverStartsACall()
    {
        DetectionConfig config = CallConfig();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        DecisionOutcome outcome = RunCapture(state, config, ref clock, 120000, Microphone("Zoom.exe", -100));

        Assert.False(outcome.WantsSilence);
        Assert.Null(state.CallApp);
    }

    /// <summary>
    /// A capture session that is open but not Active is a client that has stopped listening.
    /// It must not hold the music down.
    /// </summary>
    [Fact]
    public void AnInactiveCaptureSessionEndsTheCall()
    {
        DetectionConfig config = CallConfig();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        RunCapture(state, config, ref clock, 4000, Microphone("Zoom.exe", -18));
        Assert.NotNull(state.CallApp);

        RunCapture(state, config, ref clock, 1000, Microphone("Zoom.exe", -100, SessionActivity.Inactive));
        Assert.Null(state.CallApp);
    }

    /// <summary>
    /// The safety net. Some clients keep the capture session open after the meeting ends, and
    /// without a bound "hold while the microphone is open" would mean "hold until the
    /// application exits" — the music stranded, with no way to tell why.
    /// </summary>
    [Fact]
    public void ACallThatGoesCompletelyDeadIsTreatedAsOver()
    {
        DetectionConfig config = CallConfig();
        config.CallIdleTimeoutMs = 30000;

        var state = new DecisionState();
        DateTimeOffset clock = Start;

        RunCapture(state, config, ref clock, 4000, Microphone("Zoom.exe", -18));
        Assert.NotNull(state.CallApp);

        // The session stays open and Active, but nothing comes through it at all.
        DecisionOutcome outcome = RunCapture(state, config, ref clock, 60000, Microphone("Zoom.exe", -100));

        Assert.False(outcome.WantsSilence);
        Assert.Null(state.CallApp);
    }

    /// <summary>
    /// A listener who never unmutes still gets a call: the other end's audio is render traffic
    /// from the same application, and that keeps the hold alive past the idle timeout.
    /// </summary>
    [Fact]
    public void TheOtherEndTalkingKeepsACallAlive()
    {
        DetectionConfig config = CallConfig();
        config.CallIdleTimeoutMs = 30000;

        var state = new DecisionState();
        DateTimeOffset clock = Start;

        RunCapture(state, config, ref clock, 4000, Microphone("Zoom.exe", -18));

        // Two minutes of the microphone carrying nothing, while Zoom itself keeps playing.
        DecisionOutcome outcome = null!;
        for (int i = 0; i < 120000 / config.PollIntervalMs; i++)
        {
            var snapshot = DetectionSnapshot.Empty(clock) with
            {
                Render = [Session("Zoom.exe", -12)],
                Capture = [Microphone("Zoom.exe", -100)],
            };

            outcome = DecisionEngine.Evaluate(snapshot, config, state);
            clock = clock.AddMilliseconds(config.PollIntervalMs);
        }

        Assert.True(outcome.WantsSilence);
        Assert.Equal("In a call — Zoom.exe", outcome.Reason);
    }

    /// <summary>
    /// The longer release belongs to calls alone — a video that was paused must still come
    /// back after the ordinary five seconds.
    /// </summary>
    [Fact]
    public void OrdinaryNoiseKeepsTheShortRelease()
    {
        DetectionConfig config = CallConfig();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        Assert.True(RunFor(state, config, ref clock, 4000, Session("chrome.exe", -18)).WantsSilence);
        Assert.False(state.LastTriggerWasCall);

        Assert.False(RunFor(state, config, ref clock, 6000, Session("chrome.exe", -100)).WantsSilence);
    }

    /// <summary>
    /// An application with no call rule is still judged on level, so a microphone tool that is
    /// not a conferencing client cannot latch silence open.
    /// </summary>
    [Fact]
    public void ANonCallApplicationOnTheMicrophoneIsStillJudgedOnLevel()
    {
        DetectionConfig config = CallConfig();
        var state = new DecisionState();
        DateTimeOffset clock = Start;

        Assert.True(RunCapture(state, config, ref clock, 4000, Microphone("somerecorder.exe", -18)).WantsSilence);
        Assert.Null(state.CallApp);

        // Quiet again, and the ordinary release applies rather than the call one.
        Assert.False(RunCapture(state, config, ref clock, 6000, Microphone("somerecorder.exe", -100)).WantsSilence);
    }

    [Fact]
    public void FullscreenSignal_CountsOnlyWhenEnabled()
    {
        var config = Config();
        var state = new DecisionState();
        var snapshot = DetectionSnapshot.Empty(Start) with { Shell = ShellActivity.FullScreenD3D };

        Assert.False(DecisionEngine.Evaluate(snapshot, config, state).WantsSilence);

        config.FullscreenSignal = true;
        Assert.True(DecisionEngine.Evaluate(snapshot, config, new DecisionState()).WantsSilence);
    }
}
