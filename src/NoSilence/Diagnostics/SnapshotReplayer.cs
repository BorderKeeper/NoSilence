using NoSilence.Detection;

namespace NoSilence.Diagnostics;

/// <summary>
/// Re-runs a recorded session through the current decision engine and settings.
/// </summary>
/// <remarks>
/// This is the answer to a question no unit test can settle: <em>is -50 dBFS the right
/// threshold?</em> Record five minutes of actually playing a game, change a number, replay,
/// and see exactly where the decision flips — in a second, without relaunching anything.
/// It works because the engine takes its time from the snapshot rather than from a clock.
/// </remarks>
internal static class SnapshotReplayer
{
    /// <param name="IsInitial">
    /// True for the very first entry, which records the starting state rather than a change.
    /// Counting it as a transition made a recording that merely went play → silent → play
    /// report three transitions and trip the flapping warning.
    /// </param>
    internal sealed record Transition(DateTimeOffset At, TimeSpan Elapsed, bool Silent, string Reason, bool IsInitial = false);

    internal sealed record Result(
        int Snapshots,
        TimeSpan Duration,
        TimeSpan SilentFor,
        IReadOnlyList<Transition> Transitions)
    {
        public double SilentPercent => Duration > TimeSpan.Zero ? SilentFor / Duration * 100d : 0d;

        /// <summary>Actual changes of state, excluding the initial one.</summary>
        public int ChangeCount => Transitions.Count(t => !t.IsInitial);

        /// <summary>The worst burst of flip-flopping, as changes within any 30-second span.</summary>
        public int WorstFlapIn30Seconds
        {
            get
            {
                IReadOnlyList<Transition> changes = [.. Transitions.Where(t => !t.IsInitial)];
                int worst = 0;

                for (int i = 0; i < changes.Count; i++)
                {
                    int count = 0;
                    for (int j = i; j < changes.Count && changes[j].At - changes[i].At <= TimeSpan.FromSeconds(30); j++)
                    {
                        count++;
                    }

                    worst = Math.Max(worst, count);
                }

                return worst;
            }
        }
    }

    public static Result Run(IEnumerable<DetectionSnapshot> snapshots, DetectionConfig config)
    {
        var state = new DecisionState();
        var transitions = new List<Transition>();

        DateTimeOffset? start = null;
        DateTimeOffset last = default;
        DateTimeOffset? silentSince = null;
        TimeSpan silentFor = TimeSpan.Zero;
        bool? previous = null;
        int count = 0;

        foreach (DetectionSnapshot snapshot in snapshots)
        {
            start ??= snapshot.At;
            last = snapshot.At;
            count++;

            DecisionOutcome outcome = DecisionEngine.Evaluate(snapshot, config, state);

            if (previous != outcome.WantsSilence)
            {
                transitions.Add(new Transition(
                    snapshot.At,
                    snapshot.At - start.Value,
                    outcome.WantsSilence,
                    outcome.Reason,
                    IsInitial: previous is null));

                if (outcome.WantsSilence)
                {
                    silentSince = snapshot.At;
                }
                else if (silentSince is { } since)
                {
                    silentFor += snapshot.At - since;
                    silentSince = null;
                }

                previous = outcome.WantsSilence;
            }
        }

        if (silentSince is { } trailing)
        {
            silentFor += last - trailing;
        }

        return new Result(count, start is null ? TimeSpan.Zero : last - start.Value, silentFor, transitions);
    }
}
