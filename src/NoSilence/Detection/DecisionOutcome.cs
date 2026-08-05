namespace NoSilence.Detection;

internal enum DecisionPhase
{
    /// <summary>Music is audible.</summary>
    Playing,

    /// <summary>Something is making noise; fading out or already down.</summary>
    Ducked,

    /// <summary>Quiet again, but still inside the release window.</summary>
    Releasing,

    /// <summary>Silent because the user said so.</summary>
    Overridden,
}

/// <summary>
/// One reason, for or against silence, with enough detail to explain itself in the UI.
/// </summary>
/// <remarks>
/// Contributions include sources that did <em>not</em> count, and that is deliberate:
/// hiding the ignored ones is exactly what makes a heuristic impossible to debug. The live
/// view shows them greyed out rather than dropping them.
/// </remarks>
internal sealed record TriggerContribution(
    string Source,
    string Detail,
    bool Counts,
    double? Dbfs = null,
    int SustainedMs = 0,
    string? Rule = null,
    string? Endpoint = null,
    double? PeakDbfs = null);

/// <summary>What the engine decided, and why.</summary>
internal sealed record DecisionOutcome(
    bool WantsSilence,
    float TargetGain,
    int FadeMs,
    DecisionPhase Phase,
    string Reason,
    IReadOnlyList<TriggerContribution> Contributions)
{
    /// <summary>Seconds of continuous quiet still required before music returns. Null unless releasing.</summary>
    public double? ResumesInSeconds { get; init; }

    /// <summary>
    /// True while a call is holding the music down, so the UI can offer to play through it.
    /// </summary>
    /// <remarks>
    /// A flag rather than something parsed back out of <see cref="Reason"/>, because a UI that
    /// pattern-matches on a human-readable sentence breaks the moment the sentence is reworded.
    /// </remarks>
    public bool IsCall { get; init; }
}
