using NoSilence.Detection;

namespace NoSilence.Tests;

public class PeakMathTests
{
    [Fact]
    public void DigitalSilenceClampsToTheFloorRatherThanNegativeInfinity()
    {
        Assert.Equal(PeakMath.MinDbfs, PeakMath.ToDbfs(0));
        Assert.Equal(PeakMath.MinDbfs, PeakMath.ToDbfs(-0.5));
    }

    [Fact]
    public void FullScaleIsZeroDecibels()
    {
        Assert.Equal(0d, PeakMath.ToDbfs(1.0), 6);
    }

    [Theory]
    [InlineData(0.5, -6.02)]
    [InlineData(0.1, -20)]
    [InlineData(0.01, -40)]
    [InlineData(0.00316, -50)]
    public void KnownLevelsConvertCorrectly(double peak, double expectedDb)
    {
        Assert.Equal(expectedDb, PeakMath.ToDbfs(peak), 1);
    }

    /// <summary>
    /// The number v1 used as its threshold. It reads like a cautious small value and is
    /// nothing of the sort — this is the whole reason that heuristic misbehaved.
    /// </summary>
    [Fact]
    public void TheOldThresholdWasEightyDecibelsBelowFullScale()
    {
        Assert.Equal(-80, PeakMath.ToDbfs(0.0001f), 0);
    }

    [Fact]
    public void ConversionRoundTrips()
    {
        foreach (double db in new[] { -90d, -50d, -20d, -6d, 0d })
        {
            Assert.Equal(db, PeakMath.ToDbfs(PeakMath.FromDbfs(db)), 6);
        }
    }

    [Fact]
    public void EmaAlphaIsBoundedAndMonotonic()
    {
        double fast = PeakMath.EmaAlpha(250, 100);
        double slow = PeakMath.EmaAlpha(250, 2000);

        Assert.InRange(fast, 0d, 1d);
        Assert.InRange(slow, 0d, 1d);
        Assert.True(fast > slow, "a shorter time constant must react faster");
    }
}

public class RuleMatcherTests
{
    private static SessionObservation Session(string exe, string? displayName = null, bool systemSounds = false) => new(
        "s1", "e1", "Headphones", 42, exe, displayName, systemSounds, false,
        SessionActivity.Active, 0.1f, 1f, false);

    [Fact]
    public void AnUnmatchedApplicationGetsTheGlobalDefaults()
    {
        var config = new DetectionConfig { Rules = [] };
        ResolvedRule rule = RuleMatcher.Resolve(Session("something.exe"), config);

        Assert.Equal(RuleMode.Trigger, rule.Mode);
        Assert.Equal(config.ThresholdDb, rule.ThresholdDb);
        Assert.Equal(config.MinDurationMs, rule.MinDurationMs);
    }

    [Fact]
    public void FirstMatchWins()
    {
        var config = new DetectionConfig
        {
            Rules =
            [
                new ProcessRule("chrome.exe", RuleMatchKind.ExeName, RuleMode.Ignore),
                new ProcessRule("chrome.exe", RuleMatchKind.ExeName, RuleMode.AlwaysTrigger),
            ],
        };

        Assert.Equal(RuleMode.Ignore, RuleMatcher.Resolve(Session("chrome.exe"), config).Mode);
    }

    [Fact]
    public void DisabledRulesAreSkipped()
    {
        var config = new DetectionConfig
        {
            Rules = [new ProcessRule("chrome.exe", RuleMatchKind.ExeName, RuleMode.Ignore, Enabled: false)],
        };

        Assert.NotEqual(RuleMode.Ignore, RuleMatcher.Resolve(Session("chrome.exe"), config).Mode);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        var config = new DetectionConfig
        {
            Rules = [new ProcessRule("Chrome.EXE", RuleMatchKind.ExeName, RuleMode.Ignore)],
        };

        Assert.Equal(RuleMode.Ignore, RuleMatcher.Resolve(Session("chrome.exe"), config).Mode);
    }

    [Fact]
    public void SystemSoundsMatchByKindRatherThanName()
    {
        var config = new DetectionConfig();
        Assert.True(RuleMatcher.Resolve(Session("audiodg.exe", systemSounds: true), config).Ignored);
    }

    [Fact]
    public void DisplayNameMatchingCatchesPackagedApps()
    {
        var config = new DetectionConfig
        {
            Rules = [new ProcessRule("Calculator", RuleMatchKind.DisplayNameContains, RuleMode.Ignore)],
        };

        Assert.True(RuleMatcher.Resolve(Session("ApplicationFrameHost.exe", "Windows Calculator"), config).Ignored);
    }

    [Fact]
    public void IgnoreRulesGetAnUnreachableDurationSoTheyCanNeverFire()
    {
        var config = new DetectionConfig();
        ResolvedRule rule = RuleMatcher.Resolve(Session("explorer.exe"), config);

        Assert.True(rule.Ignored);
        Assert.Equal(int.MaxValue, rule.MinDurationMs);
    }

    [Fact]
    public void AlwaysTriggerHasNoSustainRequirement()
    {
        var config = new DetectionConfig();
        Assert.Equal(0, RuleMatcher.Resolve(Session("vlc.exe"), config).MinDurationMs);
    }

    [Theory]
    [InlineData("voicemeeter*.exe", "voicemeeter64.exe", true)]
    [InlineData("voicemeeter*.exe", "voicemeeterpro.exe", true)]
    [InlineData("voicemeeter*.exe", "vlc.exe", false)]
    [InlineData("*.exe", "anything.exe", true)]
    [InlineData("obs*", "obs64.exe", true)]
    [InlineData("chrome.exe", "chrome.exe", true)]
    [InlineData("chrome.exe", "chromium.exe", false)]
    public void GlobMatching(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, RuleMatcher.GlobMatch(pattern, value));
    }

    [Fact]
    public void AnEmptyPatternMatchesNothing()
    {
        Assert.False(RuleMatcher.GlobMatch(string.Empty, "chrome.exe"));
    }
}

public class SessionTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);

    private static DetectionConfig Config() => new() { PollIntervalMs = 250, AttackRatio = 0.7 };

    private static ResolvedRule Rule(int minDurationMs = 1000) =>
        new(RuleMode.Trigger, -50, minDurationMs, "test");

    private static SessionObservation Session(double dbfs, string id = "s1", float volume = 1f) => new(
        id, "e1", "Headphones", 42, "vlc.exe", null, false, false,
        SessionActivity.Active, (float)PeakMath.FromDbfs(dbfs), volume, false);

    [Fact]
    public void ASessionIsNotNoisyUntilTheWindowIsFull()
    {
        var tracker = new SessionTracker();
        DetectionConfig config = Config();
        DateTimeOffset clock = Start;

        // 1000 ms at 250 ms ticks is a four-sample window.
        for (int i = 0; i < 3; i++)
        {
            SessionStats stats = tracker.Observe(Session(-10), Rule(), config, clock);
            Assert.Null(stats.NoisySince);
            clock = clock.AddMilliseconds(250);
        }

        Assert.NotNull(tracker.Observe(Session(-10), Rule(), config, clock).NoisySince);
    }

    [Fact]
    public void GoingQuietClearsTheSustainClock()
    {
        var tracker = new SessionTracker();
        DetectionConfig config = Config();
        DateTimeOffset clock = Start;

        for (int i = 0; i < 8; i++, clock = clock.AddMilliseconds(250))
        {
            tracker.Observe(Session(-10), Rule(), config, clock);
        }

        // Enough quiet samples to drop the duty cycle below the ratio.
        for (int i = 0; i < 4; i++, clock = clock.AddMilliseconds(250))
        {
            tracker.Observe(Session(-100), Rule(), config, clock);
        }

        Assert.Null(tracker.TryGet("s1")!.NoisySince);
    }

    /// <summary>
    /// A brief dip must not reset the sustain clock, or speech would never qualify — that is
    /// the whole reason for a duty cycle rather than a plain threshold.
    /// </summary>
    [Fact]
    public void ASingleQuietSampleDoesNotBreakTheSustain()
    {
        var tracker = new SessionTracker();
        DetectionConfig config = Config();
        DateTimeOffset clock = Start;

        for (int i = 0; i < 8; i++, clock = clock.AddMilliseconds(250))
        {
            tracker.Observe(Session(-10), Rule(), config, clock);
        }

        DateTimeOffset? before = tracker.TryGet("s1")!.NoisySince;
        SessionStats stats = tracker.Observe(Session(-100), Rule(), config, clock);

        Assert.Equal(before, stats.NoisySince);
    }

    /// <summary>
    /// The sustain clock is backdated to the start of the window. Without that, the instant a
    /// source qualifies it would be reported as sustained "for 0.0 s".
    /// </summary>
    [Fact]
    public void TheSustainClockIsBackdatedToTheStartOfTheWindow()
    {
        var tracker = new SessionTracker();
        DetectionConfig config = Config();
        DateTimeOffset clock = Start;

        SessionStats stats = null!;
        for (int i = 0; i < 4; i++, clock = clock.AddMilliseconds(250))
        {
            stats = tracker.Observe(Session(-10), Rule(), config, clock);
        }

        Assert.True(stats.SustainedMs >= 1000, $"expected at least the window length, got {stats.SustainedMs} ms");
    }

    [Fact]
    public void ThePeakAcrossTheWindowIsRemembered()
    {
        var tracker = new SessionTracker();
        DetectionConfig config = Config();
        DateTimeOffset clock = Start;

        tracker.Observe(Session(-8), Rule(), config, clock);
        clock = clock.AddMilliseconds(250);

        for (int i = 0; i < 3; i++, clock = clock.AddMilliseconds(250))
        {
            tracker.Observe(Session(-30), Rule(), config, clock);
        }

        Assert.Equal(-8, tracker.TryGet("s1")!.WindowPeakDb, 1);
    }

    [Fact]
    public void SessionsAreForgottenOnceTheyStopBeingSeen()
    {
        var tracker = new SessionTracker { PruneAfter = TimeSpan.FromSeconds(10) };
        DetectionConfig config = Config();

        tracker.Observe(Session(-10), Rule(), config, Start);
        Assert.Equal(1, tracker.TrackedCount);

        tracker.Prune(Start.AddSeconds(5));
        Assert.Equal(1, tracker.TrackedCount);

        tracker.Prune(Start.AddSeconds(30));
        Assert.Equal(0, tracker.TrackedCount);
    }

    [Fact]
    public void SessionsAreTrackedIndependently()
    {
        var tracker = new SessionTracker();
        DetectionConfig config = Config();
        DateTimeOffset clock = Start;

        for (int i = 0; i < 6; i++, clock = clock.AddMilliseconds(250))
        {
            tracker.Observe(Session(-10, "loud"), Rule(), config, clock);
            tracker.Observe(Session(-100, "quiet"), Rule(), config, clock);
        }

        Assert.NotNull(tracker.TryGet("loud")!.NoisySince);
        Assert.Null(tracker.TryGet("quiet")!.NoisySince);
    }

    /// <summary>
    /// Session meters read after the application's own volume slider, so an app pulled down
    /// to 5% measures roughly 26 dB lower than it sounds. Compensation is opt-in.
    /// </summary>
    [Fact]
    public void SessionVolumeCompensationIsOptIn()
    {
        DetectionConfig off = Config();
        DetectionConfig on = Config();
        on.CompensateSessionVolume = true;

        var trackerOff = new SessionTracker();
        var trackerOn = new SessionTracker();

        SessionObservation quietened = Session(-40, volume: 0.1f);

        double withoutCompensation = trackerOff.Observe(quietened, Rule(), off, Start).LastDb;
        double withCompensation = trackerOn.Observe(quietened, Rule(), on, Start).LastDb;

        Assert.Equal(-40, withoutCompensation, 1);
        Assert.True(withCompensation > withoutCompensation + 15, "compensation should recover the pre-slider level");
    }

    [Fact]
    public void CompensationIsSkippedNearZeroWhereItWouldAmplifyNoise()
    {
        DetectionConfig config = Config();
        config.CompensateSessionVolume = true;

        var tracker = new SessionTracker();
        double db = tracker.Observe(Session(-60, volume: 0.001f), Rule(), config, Start).LastDb;

        Assert.Equal(-60, db, 1);
    }
}
