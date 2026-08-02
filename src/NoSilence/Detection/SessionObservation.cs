namespace NoSilence.Detection;

/// <summary>Mirror of the WASAPI session states, kept local so the detection zone stays pure.</summary>
internal enum SessionActivity
{
    Inactive,
    Active,
    Expired,
}

/// <summary>
/// What one application was doing on one endpoint at one instant.
/// </summary>
/// <remarks>
/// This is the record that replaces v1's single device-wide peak meter. Because it is
/// per-process, NoSilence can tell a Discord ping from a two-hour film, and can exclude its
/// own audio by process ID — which is what removes v1's rule that the music device had to
/// differ from the default device.
/// <para>
/// Plain, serialisable data: it is written to JSONL by <c>--diagnose</c> and read back by
/// <c>--replay</c>, so the decision engine can be re-run against a real recording.
/// </para>
/// </remarks>
/// <param name="Peak">
/// Raw peak, 0..1. Note this is measured <em>after</em> the application's own volume slider
/// and <em>before</em> the endpoint master volume.
/// </param>
internal sealed record SessionObservation(
    string SessionInstanceId,
    string EndpointId,
    string EndpointName,
    uint ProcessId,
    string ExeName,
    string? DisplayName,
    bool IsSystemSounds,
    bool IsOurProcess,
    SessionActivity State,
    float Peak,
    float SessionVolume,
    bool SessionMuted)
{
    public double Dbfs => PeakMath.ToDbfs(Peak);

    /// <summary>Best label for the UI and for log messages.</summary>
    public string Describe() => IsSystemSounds
        ? "Windows system sounds"
        : !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! : ExeName;
}
