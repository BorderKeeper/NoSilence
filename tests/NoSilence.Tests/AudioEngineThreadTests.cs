using Microsoft.Extensions.Logging.Abstractions;
using NoSilence.Audio;

namespace NoSilence.Tests;

public class AudioEngineThreadTests
{
    private static AudioEngineThread Create(int tickMs = 50) =>
        new(NullLogger<AudioEngineThread>.Instance, tickMs);

    /// <summary>
    /// Regression: Invoke&lt;T&gt; used to call itself. The lambda it passed to the Action
    /// overload was expression-bodied and therefore convertible to Func&lt;T&gt; too, so
    /// overload resolution routed it straight back into the generic method.
    /// </summary>
    [Fact]
    public void InvokeWithAResult_ReturnsItInsteadOfRecursing()
    {
        using AudioEngineThread engine = Create();
        engine.Start();

        int result = engine.Invoke(() => 6 * 7);

        Assert.Equal(42, result);
    }

    [Fact]
    public void InvokeRunsOnTheEngineThread()
    {
        using AudioEngineThread engine = Create();
        engine.Start();

        bool onEngineThread = engine.Invoke(() => engine.IsOnEngineThread);

        Assert.True(onEngineThread);
        Assert.False(engine.IsOnEngineThread);
    }

    [Fact]
    public void InvokePropagatesExceptionsToTheCaller()
    {
        using AudioEngineThread engine = Create();
        engine.Start();

        Assert.Throws<InvalidOperationException>(() => engine.Invoke(() =>
            throw new InvalidOperationException("boom")));
    }

    /// <summary>A failing tick must never take the audio engine down with it.</summary>
    [Fact]
    public void AThrowingTickDoesNotKillTheThread()
    {
        using AudioEngineThread engine = Create(20);
        int ticks = 0;

        engine.Tick += () =>
        {
            Interlocked.Increment(ref ticks);
            throw new InvalidOperationException("bad tick");
        };

        engine.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 5, TimeSpan.FromSeconds(5));

        Assert.True(Volatile.Read(ref ticks) >= 5);
        Assert.Equal(1, engine.Invoke(() => 1));
    }

    [Fact]
    public void PostedWorkRunsInOrder()
    {
        using AudioEngineThread engine = Create();
        var order = new List<int>();

        engine.Start();
        for (int i = 0; i < 20; i++)
        {
            int captured = i;
            engine.Post(() => order.Add(captured));
        }

        engine.Invoke(() => { });   // fence: everything queued before this has run

        Assert.Equal(Enumerable.Range(0, 20), order);
    }

    [Fact]
    public void TickFiresRepeatedly()
    {
        using AudioEngineThread engine = Create(20);
        int ticks = 0;
        engine.Tick += () => Interlocked.Increment(ref ticks);

        engine.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 3, TimeSpan.FromSeconds(5));

        Assert.True(Volatile.Read(ref ticks) >= 3);
    }

    [Fact]
    public void PostAfterDisposeIsIgnoredRatherThanThrowing()
    {
        AudioEngineThread engine = Create();
        engine.Start();
        engine.Dispose();

        engine.Post(() => throw new InvalidOperationException("should never run"));
    }
}
