using NoSilence.Detection;
using NoSilence.Diagnostics;

namespace NoSilence.Tests;

public class SnapshotReplayerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private static DetectionConfig Config() => new()
    {
        PollIntervalMs = 250,
        ThresholdDb = -50,
        MinDurationMs = 1000,
        ReleaseMs = 2000,
        HardDuckGraceMs = 0,
        MicrophoneSignal = false,
        FullscreenSignal = false,
    };

    private static SessionObservation Noise(double dbfs) => new(
        "session-1", "endpoint-1", "Headphones", 100, "vlc.exe", null,
        false, false, SessionActivity.Active, (float)PeakMath.FromDbfs(dbfs), 1f, false);

    /// <summary>Builds a recording: quiet, then loud, then quiet again.</summary>
    private static IEnumerable<DetectionSnapshot> Recording()
    {
        DateTimeOffset clock = Start;

        for (int i = 0; i < 20; i++, clock = clock.AddMilliseconds(250))
        {
            yield return DetectionSnapshot.Empty(clock) with { Render = [Noise(-100)] };
        }

        for (int i = 0; i < 20; i++, clock = clock.AddMilliseconds(250))
        {
            yield return DetectionSnapshot.Empty(clock) with { Render = [Noise(-15)] };
        }

        for (int i = 0; i < 40; i++, clock = clock.AddMilliseconds(250))
        {
            yield return DetectionSnapshot.Empty(clock) with { Render = [Noise(-100)] };
        }
    }

    [Fact]
    public void ReplayFindsTheDuckAndTheResume()
    {
        SnapshotReplayer.Result result = SnapshotReplayer.Run(Recording(), Config());

        Assert.Equal(80, result.Snapshots);
        Assert.Equal(2, result.ChangeCount);
        Assert.True(result.SilentFor > TimeSpan.Zero);
    }

    /// <summary>
    /// Regression: the starting state was counted as a change, so a recording that merely
    /// went play → silent → play reported three transitions and tripped the flapping warning.
    /// </summary>
    [Fact]
    public void TheStartingStateIsNotCountedAsAChange()
    {
        SnapshotReplayer.Result result = SnapshotReplayer.Run(Recording(), Config());

        Assert.True(result.Transitions[0].IsInitial);
        Assert.Equal(result.Transitions.Count - 1, result.ChangeCount);
        Assert.True(result.WorstFlapIn30Seconds < 3, "a single duck and resume is not flapping");
    }

    [Fact]
    public void AnEmptyRecordingIsHandled()
    {
        SnapshotReplayer.Result result = SnapshotReplayer.Run([], Config());

        Assert.Equal(0, result.Snapshots);
        Assert.Empty(result.Transitions);
        Assert.Equal(0, result.ChangeCount);
    }

    /// <summary>
    /// Replay has to be deterministic, or tuning against a recording would be meaningless.
    /// </summary>
    [Fact]
    public void ReplayingTheSameRecordingTwiceGivesTheSameAnswer()
    {
        SnapshotReplayer.Result first = SnapshotReplayer.Run(Recording(), Config());
        SnapshotReplayer.Result second = SnapshotReplayer.Run(Recording(), Config());

        Assert.Equal(first.ChangeCount, second.ChangeCount);
        Assert.Equal(first.SilentFor, second.SilentFor);
    }

    [Fact]
    public void AShorterReleaseProducesLessSilence()
    {
        DetectionConfig patient = Config();
        patient.ReleaseMs = 6000;

        SnapshotReplayer.Result quick = SnapshotReplayer.Run(Recording(), Config());
        SnapshotReplayer.Result slow = SnapshotReplayer.Run(Recording(), patient);

        Assert.True(slow.SilentFor > quick.SilentFor);
    }
}
