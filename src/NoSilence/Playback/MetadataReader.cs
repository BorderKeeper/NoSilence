using Microsoft.Extensions.Logging;

namespace NoSilence.Playback;

/// <summary>
/// Reads tags with TagLib#, defensively. Tag parsers meet a lot of malformed files, and a
/// background music player must never fall over because one download has a broken ID3
/// header — it just shows the file name instead.
/// </summary>
internal sealed class MetadataReader
{
    private readonly ILogger<MetadataReader> _log;

    public MetadataReader(ILogger<MetadataReader> log) => _log = log;

    public TrackInfo Read(TrackInfo track)
    {
        if (track.MetadataRead)
        {
            return track;
        }

        try
        {
            using TagLib.File file = TagLib.File.Create(track.Path);
            TagLib.Tag tag = file.Tag;

            return track with
            {
                MetadataRead = true,
                Title = Clean(tag.Title),
                Artist = Clean(tag.FirstPerformer ?? tag.FirstAlbumArtist),
                Album = Clean(tag.Album),
                Duration = file.Properties?.Duration > TimeSpan.Zero ? file.Properties.Duration : null,
                ReplayGainDb = ReadReplayGain(tag),
            };
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(ex, "No usable tags in {Path}; falling back to the file name.", track.Path);
            return track with { MetadataRead = true };
        }
    }

    /// <summary>
    /// Honours a ReplayGain tag when the file already carries one — free loudness matching
    /// for anyone who has run rsgain or foobar2000 over their library. We deliberately do
    /// not compute it ourselves; see docs/DETECTION.md.
    /// </summary>
    private static double? ReadReplayGain(TagLib.Tag tag)
    {
        double gain = tag.ReplayGainTrackGain;
        if (double.IsNaN(gain) || gain == 0d)
        {
            return null;
        }

        // Clamp: a bogus tag should not be able to blow the output up or mute a track.
        return Math.Clamp(gain, -12d, 12d);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
