using NoSilence.Detection;
using NoSilence.Tv;

namespace NoSilence.Tests;

/// <summary>
/// The failure mode this guards against is a television switching itself on in the middle of
/// the night, so every guard gets an explicit test.
/// </summary>
public class TvPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Long enough before <see cref="Start"/> that the default input is in steady state. The
    /// startup rule is a separate set of tests and must not leak into the others.
    /// </summary>
    private static readonly DateTimeOffset LaunchedLongAgo = Start.AddHours(-1);

    private static TvPolicyConfig Config() => new()
    {
        WakeEnabled = true,
        SleepEnabled = true,
        RequireWantsToPlayForMs = 120000,
        CooldownMs = 300000,
        MaxPowerCommandsPerHour = 6,
        UserVetoMinutes = 60,
        SleepAfterMs = 1800000,
        OnlySleepIfWeWokeIt = true,
        WakeAtStartup = true,
        StartupWindowMs = 300000,
        StartupWakeAfterMs = 15000,
    };

    private static TvPolicyInput Input(
        DateTimeOffset now,
        bool wantsToPlay = true,
        bool endpointPresent = false,
        bool library = true,
        OperatingMode mode = OperatingMode.Auto,
        bool snoozed = false,
        DisplayCapabilities capabilities = DisplayCapabilities.Wake | DisplayCapabilities.Sleep,
        DateTimeOffset? startedAt = null,
        DisplayPowerState? reportedPower = null) =>
        new(now, wantsToPlay, endpointPresent, library, mode, snoozed, capabilities, startedAt ?? LaunchedLongAgo, reportedPower);

    /// <summary>Runs the policy forward in one-minute steps and returns the first action taken.</summary>
    private static (TvAction Action, DateTimeOffset At) RunUntilAction(
        TvPolicyConfig config,
        TvPolicyState state,
        Func<DateTimeOffset, TvPolicyInput> input,
        int minutes)
    {
        for (int i = 0; i <= minutes; i++)
        {
            DateTimeOffset now = Start.AddMinutes(i);
            TvAction action = TvPolicy.Decide(input(now), config, state);
            if (action != TvAction.None)
            {
                return (action, now);
            }
        }

        return (TvAction.None, Start.AddMinutes(minutes));
    }

    [Fact]
    public void DoesNothingWhenWakeIsDisabled()
    {
        TvPolicyConfig config = Config();
        config.WakeEnabled = false;

        (TvAction action, _) = RunUntilAction(config, new TvPolicyState(), now => Input(now), 30);

        Assert.Equal(TvAction.None, action);
    }

    [Fact]
    public void DoesNothingWhenTheControllerCannotWake()
    {
        (TvAction action, _) = RunUntilAction(
            Config(),
            new TvPolicyState(),
            now => Input(now, capabilities: DisplayCapabilities.None),
            30);

        Assert.Equal(TvAction.None, action);
    }

    /// <summary>A brief lull between videos must never power-cycle a television.</summary>
    [Fact]
    public void WakesOnlyAfterWantingToPlayForTheFullPeriod()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState();

        // One minute is short of the two-minute requirement.
        Assert.Equal(TvAction.None, TvPolicy.Decide(Input(Start), config, state));
        Assert.Equal(TvAction.None, TvPolicy.Decide(Input(Start.AddMinutes(1)), config, state));

        Assert.Equal(TvAction.Wake, TvPolicy.Decide(Input(Start.AddMinutes(3)), config, state));
    }

    [Fact]
    public void AnInterruptionResetsTheWakeCountdown()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState();

        TvPolicy.Decide(Input(Start), config, state);
        TvPolicy.Decide(Input(Start.AddSeconds(90), wantsToPlay: false), config, state);

        // The clock restarts, so ninety more seconds is not enough.
        Assert.Equal(TvAction.None, TvPolicy.Decide(Input(Start.AddSeconds(100)), config, state));
        Assert.Equal(TvAction.None, TvPolicy.Decide(Input(Start.AddSeconds(180)), config, state));
        Assert.Equal(TvAction.Wake, TvPolicy.Decide(Input(Start.AddSeconds(230)), config, state));
    }

    /// <summary>
    /// If the endpoint is present the television is already on. Sending a power command then
    /// is how an app turns a TV off by mistake.
    /// </summary>
    [Fact]
    public void NeverWakesWhenTheOutputEndpointIsAlreadyPresent()
    {
        (TvAction action, _) = RunUntilAction(Config(), new TvPolicyState(), now => Input(now, endpointPresent: true), 30);

        Assert.NotEqual(TvAction.Wake, action);
    }

    [Fact]
    public void NeverWakesWithAnEmptyLibrary()
    {
        (TvAction action, _) = RunUntilAction(Config(), new TvPolicyState(), now => Input(now, library: false), 30);

        Assert.Equal(TvAction.None, action);
    }

    [Fact]
    public void NeverWakesWhenSetToAlwaysSilent()
    {
        (TvAction action, _) = RunUntilAction(
            Config(),
            new TvPolicyState(),
            now => Input(now, mode: OperatingMode.AlwaysSilent),
            30);

        Assert.Equal(TvAction.None, action);
    }

    [Fact]
    public void NeverWakesWhileSnoozed()
    {
        (TvAction action, _) = RunUntilAction(
            Config(),
            new TvPolicyState(),
            now => Input(now, snoozed: true),
            30);

        Assert.Equal(TvAction.None, action);
    }

    [Fact]
    public void RespectsTheCooldownBetweenPowerCommands()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState();

        // The policy needs history before it will act, so drive it forward to the first wake.
        (TvAction first, DateTimeOffset firstAt) = RunUntilAction(config, state, now => Input(now), 10);
        Assert.Equal(TvAction.Wake, first);
        TvPolicy.RecordPowerCommand(firstAt, state);

        // Five-minute cooldown: nothing for the next four minutes, though conditions hold.
        for (int minute = 1; minute <= 4; minute++)
        {
            Assert.Equal(TvAction.None, TvPolicy.Decide(Input(firstAt.AddMinutes(minute)), config, state));
        }
    }

    /// <summary>The circuit breaker: nothing gets to send more than the hourly cap.</summary>
    [Fact]
    public void TheHourlyCapStopsRunawayPowerCommands()
    {
        TvPolicyConfig config = Config();
        config.CooldownMs = 0;
        var state = new TvPolicyState();

        int wakes = 0;
        for (int minute = 0; minute < 60; minute++)
        {
            DateTimeOffset now = Start.AddMinutes(minute);
            if (TvPolicy.Decide(Input(now), config, state) == TvAction.Wake)
            {
                wakes++;
                TvPolicy.RecordPowerCommand(now, state);
            }
        }

        Assert.Equal(config.MaxPowerCommandsPerHour, wakes);
    }

    /// <summary>
    /// The "don't fight the user" rule. Without it, switching the TV off by hand starts an
    /// argument: the app wakes it, you turn it off, repeat.
    /// </summary>
    [Fact]
    public void SwitchingTheTelevisionOffByHandSuppressesWakingForAnHour()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState();

        TvPolicy.NoteUnexpectedDisappearance(Start, config, state);

        (TvAction action, _) = RunUntilAction(config, state, now => Input(now), 55);
        Assert.Equal(TvAction.None, action);

        // Past the hour, normal behaviour resumes.
        var later = new TvPolicyInput(
            Start.AddMinutes(61), true, false, true, OperatingMode.Auto, false, DisplayCapabilities.Wake, LaunchedLongAgo);
        TvPolicy.Decide(later, config, state);
        Assert.Equal(TvAction.Wake, TvPolicy.Decide(
            later with { Now = Start.AddMinutes(64) }, config, state));
    }

    [Fact]
    public void AskingToWakeExplicitlyClearsTheVeto()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState();

        TvPolicy.NoteUnexpectedDisappearance(Start, config, state);
        Assert.NotNull(state.UserVetoUntil);

        TvPolicy.ClearVeto(state);

        Assert.Null(state.UserVetoUntil);
    }

    /// <summary>We never turn off a television the user switched on themselves.</summary>
    [Fact]
    public void DoesNotSleepATelevisionWeDidNotWake()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState { WeWokeIt = false };

        (TvAction action, _) = RunUntilAction(
            config,
            state,
            now => Input(now, wantsToPlay: false, endpointPresent: true),
            90);

        Assert.Equal(TvAction.None, action);
    }

    [Fact]
    public void SleepsAfterTheIdlePeriodWhenWeWokeIt()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState { WeWokeIt = true };

        (TvAction action, DateTimeOffset at) = RunUntilAction(
            config,
            state,
            now => Input(now, wantsToPlay: false, endpointPresent: true),
            90);

        Assert.Equal(TvAction.Sleep, action);
        Assert.True(at - Start >= TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void NeverSleepsWhenTheTelevisionIsAlreadyOff()
    {
        TvPolicyConfig config = Config();
        config.WakeEnabled = false;
        var state = new TvPolicyState { WeWokeIt = true };

        (TvAction action, _) = RunUntilAction(
            config,
            state,
            now => Input(now, wantsToPlay: false, endpointPresent: false),
            90);

        Assert.Equal(TvAction.None, action);
    }

    [Fact]
    public void MusicStartingAgainCancelsAPendingSleep()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState { WeWokeIt = true };

        for (int minute = 0; minute < 25; minute++)
        {
            TvPolicy.Decide(Input(Start.AddMinutes(minute), wantsToPlay: false, endpointPresent: true), config, state);
        }

        // Music resumes at minute 25, which resets the idle clock.
        TvPolicy.Decide(Input(Start.AddMinutes(25), wantsToPlay: true, endpointPresent: true), config, state);

        Assert.Equal(TvAction.None, TvPolicy.Decide(
            Input(Start.AddMinutes(35), wantsToPlay: false, endpointPresent: true), config, state));
    }

    // ---- starting up -----------------------------------------------------

    /// <summary>
    /// The two-minute rule guards against a gap between videos. A launch is not one, so
    /// sitting down at a machine that has just come up does not mean waiting it out.
    /// </summary>
    [Fact]
    public void WakesSoonAfterStartupWithoutTheFullWait()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState();

        Assert.Equal(TvAction.None, TvPolicy.Decide(Input(Start, startedAt: Start), config, state));
        Assert.Equal(TvAction.None, TvPolicy.Decide(Input(Start.AddSeconds(10), startedAt: Start), config, state));
        Assert.Equal(TvAction.Wake, TvPolicy.Decide(Input(Start.AddSeconds(20), startedAt: Start), config, state));
    }

    /// <summary>
    /// Deliberately independent of <see cref="TvPolicyConfig.WakeEnabled"/>: one attempt at
    /// launch is predictable in a way that waking at any moment of the day is not.
    /// </summary>
    [Fact]
    public void WakesAtStartupEvenWhenAutomaticWakingIsOff()
    {
        TvPolicyConfig config = Config();
        config.WakeEnabled = false;
        var state = new TvPolicyState();

        TvPolicy.Decide(Input(Start, startedAt: Start), config, state);

        Assert.Equal(TvAction.Wake, TvPolicy.Decide(Input(Start.AddSeconds(20), startedAt: Start), config, state));
    }

    [Fact]
    public void TheStartupWakeCanBeTurnedOff()
    {
        TvPolicyConfig config = Config();
        config.WakeAtStartup = false;
        var state = new TvPolicyState();

        TvPolicy.Decide(Input(Start, startedAt: Start), config, state);

        // Back to the ordinary rule, which is nowhere near satisfied twenty seconds in.
        Assert.Equal(TvAction.None, TvPolicy.Decide(Input(Start.AddSeconds(20), startedAt: Start), config, state));
    }

    /// <summary>Once the window has passed, only the ordinary rule is left.</summary>
    [Fact]
    public void TheStartupWakeStopsAtTheEndOfTheWindow()
    {
        TvPolicyConfig config = Config();
        config.WakeEnabled = false;
        var state = new TvPolicyState();

        for (int minute = 6; minute <= 30; minute++)
        {
            Assert.Equal(TvAction.None, TvPolicy.Decide(
                Input(Start.AddMinutes(minute), startedAt: Start), config, state));
        }
    }

    /// <summary>
    /// The rule that matters most here. Switching the set off by hand and then restarting
    /// NoSilence — or having it restart at the next logon — must not turn it back on, and the
    /// veto is persisted precisely so that it survives that.
    /// </summary>
    [Fact]
    public void TheStartupWakeStillRespectsAManualPowerOff()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState { UserVetoUntil = Start.AddMinutes(50) };

        for (int i = 0; i <= 5; i++)
        {
            Assert.Equal(TvAction.None, TvPolicy.Decide(
                Input(Start.AddMinutes(i), startedAt: Start), config, state));
        }
    }

    /// <summary>
    /// A machine that comes up into a call or a game has nothing to play, so there is nothing
    /// to turn a television on for.
    /// </summary>
    [Fact]
    public void TheStartupWakeWaitsForSomethingToPlay()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState();

        for (int i = 0; i <= 60; i += 10)
        {
            Assert.Equal(TvAction.None, TvPolicy.Decide(
                Input(Start.AddSeconds(i), wantsToPlay: false, startedAt: Start), config, state));
        }

        // The call ends inside the window: the shortened wait starts from there, not from launch.
        Assert.Equal(TvAction.None, TvPolicy.Decide(Input(Start.AddSeconds(65), startedAt: Start), config, state));
        Assert.Equal(TvAction.Wake, TvPolicy.Decide(Input(Start.AddSeconds(85), startedAt: Start), config, state));
    }

    /// <summary>The television is already on, which is the whole reason to trust the endpoint.</summary>
    [Fact]
    public void NeverWakesAtStartupWhenTheOutputEndpointIsAlreadyPresent()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState();

        for (int i = 0; i <= 120; i += 20)
        {
            Assert.Equal(TvAction.None, TvPolicy.Decide(
                Input(Start.AddSeconds(i), endpointPresent: true, startedAt: Start), config, state));
        }
    }

    /// <summary>
    /// The case this was written for, taken from a real log: the set was in standby at 12:36
    /// while Windows still had the HDMI endpoint Active and NoSilence was happily "playing" to
    /// it. Trusting the endpoint means never waking the television that needed waking.
    /// </summary>
    [Fact]
    public void TheTelevisionsOwnReportOutranksTheAudioEndpoint()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState();

        TvPolicy.Decide(
            Input(Start, endpointPresent: true, startedAt: Start, reportedPower: DisplayPowerState.Standby),
            config,
            state);

        Assert.Equal(TvAction.Wake, TvPolicy.Decide(
            Input(Start.AddSeconds(20), endpointPresent: true, startedAt: Start, reportedPower: DisplayPowerState.Standby),
            config,
            state));
    }

    /// <summary>
    /// A set that is on but showing a games console has no HDMI endpoint for this PC. Its own
    /// report is what stops a power command being sent into that.
    /// </summary>
    [Fact]
    public void ATelevisionThatSaysItIsOnIsLeftAlone()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState();

        for (int i = 0; i <= 120; i += 20)
        {
            Assert.Equal(TvAction.None, TvPolicy.Decide(
                Input(Start.AddSeconds(i), startedAt: Start, reportedPower: DisplayPowerState.On),
                config,
                state));
        }
    }

    [Fact]
    public void AnUnreachableTelevisionFallsBackToTheAudioEndpoint()
    {
        TvPolicyConfig config = Config();
        var state = new TvPolicyState();

        for (int i = 0; i <= 120; i += 20)
        {
            Assert.Equal(TvAction.None, TvPolicy.Decide(
                Input(Start.AddSeconds(i), endpointPresent: true, startedAt: Start, reportedPower: DisplayPowerState.Unreachable),
                config,
                state));
        }
    }

    /// <summary>
    /// The persisted state carries a "wanting to play since" from the previous run. Left
    /// alone it satisfies the two-minute rule on the first tick after launch, which would
    /// send a power command before anything had been observed at all.
    /// </summary>
    [Fact]
    public void ATimerFromTheLastRunDoesNotCountTowardsTheWait()
    {
        TvPolicyConfig config = Config();
        config.WakeAtStartup = false;
        var state = new TvPolicyState { WantsToPlaySince = Start.AddHours(-5), IdleSince = Start.AddHours(-5) };

        TvPolicy.BeginSession(state);

        Assert.Null(state.WantsToPlaySince);
        Assert.Null(state.IdleSince);
        Assert.Equal(TvAction.None, TvPolicy.Decide(Input(Start, startedAt: Start), config, state));
    }
}
