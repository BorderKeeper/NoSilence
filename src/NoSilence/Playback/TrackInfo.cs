namespace NoSilence.Playback;

/// <summary>
/// One playable file. Metadata is optional and filled in lazily — reading tags for a
/// four-thousand-file library up front costs seconds of dead air before the first note.
/// </summary>
internal sealed record TrackInfo(string Path, long FileSize, DateTime LastWriteUtc)
{
    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>Track gain in dB from a ReplayGain tag, when the file carries one.</summary>
    public double? ReplayGainDb { get; init; }

    /// <summary>True once tags have been read, successfully or not, so we only try once.</summary>
    public bool MetadataRead { get; init; }

    /// <summary>Best available display name: tags if we have them, otherwise the file name.</summary>
    public string DisplayName => (Title, Artist) switch
    {
        (not null, not null) => $"{Artist} — {Title}",
        (not null, null) => Title,
        _ => System.IO.Path.GetFileNameWithoutExtension(Path),
    };

    public static TrackInfo FromFile(FileInfo file) => new(file.FullName, file.Length, file.LastWriteTimeUtc);
}
