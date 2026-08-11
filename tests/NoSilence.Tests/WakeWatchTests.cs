using NoSilence.Tv;

namespace NoSilence.Tests;

/// <summary>
/// The three signals that turn the television on after a wake. A false positive here is a power
/// command nobody asked for; a false negative is the feature not existing. Both have happened.
/// </summary>
public class WakeWatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 11, 21, 0, 0, TimeSpan.Zero);

    /// <summary>Somebody at the keyboard: the idle signal fires on the edge out of a long gap.</summary>
    private static readonly TimeSpan Active = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan AllNight = TimeSpan.FromHours(11);

    private static WakeWatch Watch() => new();

    /// <summary>The ordinary case: ticking along every five seconds is not a wake.</summary>
    [Fact]
    public void SteadyTickingIsNotAWake()
    {
        WakeWatch watch = Watch();

        for (int i = 0; i < 200; i++)
        {
            WakeReason reason = watch.Observe(Start.AddSeconds(i * 5), true, Active);
            Assert.Equal(WakeReason.None, reason);
        }
    }

    /// <summary>
    /// The first observation establishes a baseline. Reporting a wake here would reopen the
    /// startup window that launching has just opened.
    /// </summary>
    [Fact]
    public void TheFirstObservationIsNeverAWake()
    {
        Assert.Equal(WakeReason.None, Watch().Observe(Start, false, AllNight));
    }

    /// <summary>A frozen process: eleven hours passed between two consecutive ticks.</summary>
    [Fact]
    public void AClockJumpIsAWake()
    {
        WakeWatch watch = Watch();

        watch.Observe(Start, true, Active);

        Assert.Equal(WakeReason.ClockJumped, watch.Observe(Start.AddHours(11), true, Active));
    }

    [Fact]
    public void AClockJumpIsReportedOnlyOnce()
    {
        WakeWatch watch = Watch();

        watch.Observe(Start, true, Active);
        watch.Observe(Start.AddHours(11), true, Active);

        Assert.Equal(WakeReason.None, watch.Observe(Start.AddHours(11).AddSeconds(5), true, Active));
    }

    /// <summary>
    /// A stalled tick is not a suspend. Ninety seconds of head-room exists so a loaded machine
    /// or a debugger break cannot hand the television a power command.
    /// </summary>
    [Fact]
    public void AMerelySlowTickIsNotAWake()
    {
        WakeWatch watch = Watch();

        watch.Observe(Start, true, Active);

        Assert.Equal(WakeReason.None, watch.Observe(Start.AddSeconds(60), true, Active));
    }

    /// <summary>The television has been off for hours and has just come back on.</summary>
    [Fact]
    public void AnEndpointReturningAfterHoursIsAWake()
    {
        WakeWatch watch = Watch();
        var whileAway = new List<WakeReason>();

        watch.Observe(Start, true, Active);

        for (int minute = 1; minute <= 12 * 60; minute++)
        {
            whileAway.Add(watch.Observe(Start.AddMinutes(minute), false, Active));
        }

        // One minute after the last tick, not two: the process kept ticking all night, so a gap
        // large enough to look like a suspend would be a different signal entirely.
        WakeReason morning = watch.Observe(Start.AddMinutes(721), true, Active);

        Assert.Equal(WakeReason.OutputReturned, morning);
        Assert.DoesNotContain(WakeReason.OutputReturned, whileAway);
    }

    /// <summary>
    /// The endpoint flap that used to be mistaken for a manual power-off: gone and back inside
    /// five seconds, three times in one morning's log.
    /// </summary>
    [Fact]
    public void AnEndpointFlapIsNotAWake()
    {
        WakeWatch watch = Watch();

        watch.Observe(Start, true, Active);
        watch.Observe(Start.AddSeconds(5), false, Active);

        Assert.Equal(WakeReason.None, watch.Observe(Start.AddSeconds(10), true, Active));
    }

    /// <summary>
    /// Away Mode with the set switched off, which is what the logs say actually happens: the
    /// process runs all night, so the clock never jumps, and the endpoint flaps for six seconds
    /// in the evening rather than going away. Input in the morning is the only edge left.
    /// </summary>
    [Fact]
    public void InputAfterANightOfNothingIsAWake()
    {
        WakeWatch watch = Watch();

        watch.Observe(Start, true, Active);

        // Twelve hours of the machine sitting there, idle climbing, endpoint present throughout.
        for (int minute = 1; minute <= 12 * 60; minute++)
        {
            WakeReason reason = watch.Observe(Start.AddMinutes(minute), true, TimeSpan.FromMinutes(minute));
            Assert.Equal(WakeReason.None, reason);
        }

        Assert.Equal(WakeReason.UserReturned, watch.Observe(Start.AddMinutes(721), true, Active));
    }

    [Fact]
    public void TheUserReturningIsReportedOnlyOnce()
    {
        WakeWatch watch = Watch();

        watch.Observe(Start, true, AllNight);
        Assert.Equal(WakeReason.UserReturned, watch.Observe(Start.AddSeconds(5), true, Active));
        Assert.Equal(WakeReason.None, watch.Observe(Start.AddSeconds(10), true, Active));
    }

    /// <summary>
    /// A coffee, a phone call or a long read must not count. Fifteen minutes is the line, and
    /// the point of putting it there is that ordinary pauses stay below it.
    /// </summary>
    [Fact]
    public void AShortAbsenceFromTheKeyboardIsNotAWake()
    {
        WakeWatch watch = Watch();

        watch.Observe(Start, true, TimeSpan.FromMinutes(9));

        Assert.Equal(WakeReason.None, watch.Observe(Start.AddSeconds(5), true, Active));
    }

    /// <summary>
    /// A real suspend satisfies all three at once. That has to be one wake: the others would
    /// land inside the first one's cooldown and be dropped with a warning.
    /// </summary>
    [Fact]
    public void ASuspendWithTheTelevisionOffReportsASingleWake()
    {
        WakeWatch watch = Watch();

        watch.Observe(Start, true, Active);
        watch.Observe(Start.AddSeconds(5), false, Active);

        // Idle is small on the far side because a key press is what woke the machine — that is
        // what `powercfg /lastwake` reports on this hardware, a USB controller.
        Assert.Equal(WakeReason.ClockJumped, watch.Observe(Start.AddHours(11), true, Active));
        Assert.Equal(WakeReason.None, watch.Observe(Start.AddHours(11).AddSeconds(5), true, Active));
    }

    /// <summary>
    /// A machine that wakes with the television still off must not report a second wake when the
    /// set is finally switched on minutes later.
    /// </summary>
    [Fact]
    public void TheTelevisionComingOnLaterIsNotASecondWake()
    {
        WakeWatch watch = Watch();

        watch.Observe(Start, true, Active);
        Assert.Equal(WakeReason.ClockJumped, watch.Observe(Start.AddHours(11), false, Active));

        for (int minute = 1; minute <= 10; minute++)
        {
            WakeReason reason = watch.Observe(Start.AddHours(11).AddMinutes(minute), false, Active);
            Assert.Equal(WakeReason.None, reason);
        }

        // Ten minutes of absence is short of the five-minute-plus-a-wake rule being a *new*
        // event, but long enough that it does count once the set appears — one wake, then this.
        Assert.Equal(WakeReason.OutputReturned, watch.Observe(Start.AddHours(11).AddMinutes(11), true, Active));
    }
}
