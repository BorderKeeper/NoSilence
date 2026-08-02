namespace NoSilence.Detection;

/// <summary>
/// Rolling per-session statistics. Pure: it is fed snapshots and a clock value, never a
/// real one.
/// </summary>
/// <remarks>
/// The duty-cycle window lives here. Each session keeps a bitmask of its recent
/// above/below-threshold samples; a source counts as noisy when enough of the trailing
/// window is above. This is what separates a Discord ping from a Discord call using nothing
/// but duration, and it is why the sustain requirement is expressed in milliseconds rather
/// than "one loud sample".
/// </remarks>
internal sealed class SessionTracker
{
    /// <summary>A uint mask holds 32 samples — eight seconds at the default 250 ms tick.</summary>
    private const int MaxWindowSamples = 32;

    private readonly Dictionary<string, SessionStats> _stats = new(StringComparer.Ordinal);

    /// <summary>Sessions unseen for this long are forgotten, so the dictionary cannot grow without bound.</summary>
    public TimeSpan PruneAfter { get; init; } = TimeSpan.FromSeconds(30);

    public int TrackedCount => _stats.Count;

    public SessionStats? TryGet(string sessionInstanceId) =>
        _stats.TryGetValue(sessionInstanceId, out SessionStats? stats) ? stats : null;

    /// <summary>
    /// Folds one observation into that session's history and returns the updated stats.
    /// </summary>
    public SessionStats Observe(SessionObservation session, ResolvedRule rule, DetectionConfig config, DateTimeOffset at)
    {
        if (!_stats.TryGetValue(session.SessionInstanceId, out SessionStats? stats))
        {
            stats = new SessionStats();
            _stats[session.SessionInstanceId] = stats;
        }

        double peak = session.Peak;

        // Undo the application's own volume slider, when asked. Guarded near zero, where
        // the division would turn dither into a false trigger.
        if (config.CompensateSessionVolume && session.SessionVolume >= 0.05f)
        {
            peak /= session.SessionVolume;
        }

        double db = PeakMath.ToDbfs(peak);
        double alpha = PeakMath.EmaAlpha(config.PollIntervalMs, 500);
        stats.SmoothedDb = stats.HasHistory ? (alpha * db) + ((1 - alpha) * stats.SmoothedDb) : db;
        stats.HasHistory = true;
        stats.LastDb = db;
        stats.LastSeenAt = at;

        bool above = db > rule.ThresholdDb;
        if (above)
        {
            stats.LastAboveAt = at;
        }

        int window = WindowSamples(rule.MinDurationMs, config.PollIntervalMs);
        stats.Push(above, window);

        bool noisy = rule.Mode == RuleMode.AlwaysTrigger
            ? above
            : stats.SamplesSeen >= window && stats.AboveRatio(window) >= config.AttackRatio;

        if (noisy)
        {
            // Backdate to the start of the window rather than to now. By the time a source
            // qualifies it has already been noisy for the whole window, and dating it from
            // this instant would make the UI announce a duck "for 0.0 s".
            stats.NoisySince ??= at.AddMilliseconds(-window * config.PollIntervalMs);
        }
        else
        {
            stats.NoisySince = null;
        }

        stats.SustainedMs = stats.NoisySince is { } since ? (int)(at - since).TotalMilliseconds : 0;
        return stats;
    }

    /// <summary>
    /// How many samples the trailing window holds. The window <em>is</em> the sustain
    /// requirement, so "noisy for MinDurationMs" and "above for most of the last
    /// MinDurationMs" are the same statement.
    /// </summary>
    private static int WindowSamples(int minDurationMs, int pollIntervalMs)
    {
        if (minDurationMs <= 0 || pollIntervalMs <= 0)
        {
            return 1;
        }

        int samples = (int)Math.Ceiling((double)minDurationMs / pollIntervalMs);
        return Math.Clamp(samples, 1, MaxWindowSamples);
    }

    public void Prune(DateTimeOffset at)
    {
        if (_stats.Count == 0)
        {
            return;
        }

        List<string>? dead = null;
        foreach ((string id, SessionStats stats) in _stats)
        {
            if (at - stats.LastSeenAt > PruneAfter)
            {
                (dead ??= []).Add(id);
            }
        }

        if (dead is null)
        {
            return;
        }

        foreach (string id in dead)
        {
            _stats.Remove(id);
        }
    }

    public void Clear() => _stats.Clear();
}

/// <summary>Rolling state for one audio session.</summary>
internal sealed class SessionStats
{
    private uint _history;

    /// <summary>Most recent level in dBFS.</summary>
    public double LastDb { get; internal set; } = PeakMath.MinDbfs;

    /// <summary>Level smoothed with a ~500 ms time constant. Steadier to display.</summary>
    public double SmoothedDb { get; internal set; } = PeakMath.MinDbfs;

    public bool HasHistory { get; internal set; }

    public int SamplesSeen { get; private set; }

    public DateTimeOffset LastSeenAt { get; internal set; }

    public DateTimeOffset? LastAboveAt { get; internal set; }

    /// <summary>When this session last became consistently noisy, or null if it is not.</summary>
    public DateTimeOffset? NoisySince { get; internal set; }

    /// <summary>How long it has been consistently noisy. Zero when it is not.</summary>
    public int SustainedMs { get; internal set; }

    internal void Push(bool above, int window)
    {
        _history = (_history << 1) | (above ? 1u : 0u);
        SamplesSeen = Math.Min(SamplesSeen + 1, window);
    }

    /// <summary>Fraction of the trailing <paramref name="window"/> samples that were above threshold.</summary>
    public double AboveRatio(int window)
    {
        if (window <= 0)
        {
            return 0d;
        }

        uint mask = window >= 32 ? uint.MaxValue : (1u << window) - 1;
        return (double)System.Numerics.BitOperations.PopCount(_history & mask) / window;
    }
}
