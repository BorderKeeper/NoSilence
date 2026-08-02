using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace NoSilence.Playback;

/// <summary>
/// The never-ending source at the head of the graph.
/// </summary>
/// <remarks>
/// <see cref="Read"/> never returns zero. When a track ends it disposes that reader, opens
/// the next one, and carries on filling <em>the same buffer the sink asked for</em> — so the
/// device never sees a short read, never stops, and the transition has no gap at the device
/// level. This is the structural fix for v1, where a finished track left the output in the
/// <c>Stopped</c> state and the main loop called <c>Play()</c> on it every 500 ms forever.
/// <para>
/// Skip commands are lock-free (an interlocked counter consumed by the audio thread) so a
/// menu click can never block on disk I/O, and the audio thread can never block on the UI.
/// </para>
/// </remarks>
internal sealed class PlaylistSampleProvider : ISampleProvider
{
    /// <summary>
    /// How many consecutive files may fail to open inside a single <see cref="Read"/> before
    /// we give up and report a stall. Without a bound, a library of unreadable files spins
    /// the audio thread at 100% forever.
    /// </summary>
    private const int MaxConsecutiveOpenFailures = 10;

    private readonly ShuffleQueue _queue;
    private readonly TrackReaderFactory _factory;
    private readonly MusicLibrary _library;
    private readonly MetadataReader _metadata;
    private readonly ILogger<PlaylistSampleProvider> _log;

    private TrackReader? _reader;
    private int _pendingSkip;
    private volatile bool _stalled;

    public PlaylistSampleProvider(
        ShuffleQueue queue,
        TrackReaderFactory factory,
        MusicLibrary library,
        MetadataReader metadata,
        ILogger<PlaylistSampleProvider> log)
    {
        _queue = queue;
        _factory = factory;
        _library = library;
        _metadata = metadata;
        _log = log;
    }

    public WaveFormat WaveFormat => TrackReaderFactory.PipelineFormat;

    /// <summary>The track currently being decoded, or null if nothing is open.</summary>
    public TrackInfo? CurrentTrack { get; private set; }

    public TimeSpan Position => _reader?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration => _reader?.Duration ?? TimeSpan.Zero;

    /// <summary>True when nothing in the library could be opened. The tray shows an error state.</summary>
    public bool IsStalled => _stalled;

    /// <summary>Raised on the audio thread when a new track starts. Handlers must marshal.</summary>
    public event EventHandler<TrackInfo>? TrackChanged;

    /// <summary>Raised on the audio thread when we ran out of playable files.</summary>
    public event EventHandler<string>? Stalled;

    public void Next() => Interlocked.Increment(ref _pendingSkip);

    public void Previous() => Interlocked.Decrement(ref _pendingSkip);

    /// <summary>
    /// Tells the playlist the library was rescanned.
    /// </summary>
    /// <remarks>
    /// Deliberately does <em>not</em> touch the open reader. The track that is playing keeps
    /// playing — <see cref="ShuffleQueue.Rebuild"/> has already preserved its position, and
    /// a rescan fires whenever a file lands anywhere in a watched folder, so interrupting
    /// here would restart the current song at random. All this does is clear a stall so a
    /// previously empty library gets retried.
    /// </remarks>
    public void OnLibraryChanged() => _stalled = false;

    public int Read(float[] buffer, int offset, int count)
    {
        int filled = 0;
        int failures = 0;

        ApplyPendingSkips();

        while (filled < count)
        {
            if (_reader is null)
            {
                if (!OpenNext(advance: false))
                {
                    failures++;
                    if (failures >= MaxConsecutiveOpenFailures || _queue.Count == 0)
                    {
                        ReportStall(failures);
                        break;
                    }

                    continue;
                }

                failures = 0;
            }

            int read = _reader!.Samples.Read(buffer, offset + filled, count - filled);
            if (read > 0)
            {
                filled += read;
                continue;
            }

            // End of track: swap readers in place and keep filling this same buffer.
            if (!OpenNext(advance: true))
            {
                failures++;
                if (failures >= MaxConsecutiveOpenFailures || _queue.Count == 0)
                {
                    ReportStall(failures);
                    break;
                }
            }
            else
            {
                failures = 0;
            }
        }

        // Always hand back a full buffer. A short read tells WasapiOut the stream ended.
        if (filled < count)
        {
            Array.Clear(buffer, offset + filled, count - filled);
        }

        return count;
    }

    private void ApplyPendingSkips()
    {
        int skips = Interlocked.Exchange(ref _pendingSkip, 0);
        if (skips == 0)
        {
            return;
        }

        CloseReader();

        for (int i = 0; i < skips; i++)
        {
            _queue.Next();
        }

        for (int i = 0; i > skips; i--)
        {
            _queue.Previous();
        }

        _stalled = false;
    }

    private bool OpenNext(bool advance)
    {
        CloseReader();

        TrackInfo? track = advance || _queue.Current is null ? _queue.Next() : _queue.Current;
        if (track is null)
        {
            return false;
        }

        TrackReader? reader = _factory.TryOpen(track, out string? error);
        if (reader is null)
        {
            _library.MarkUnreadable(track.Path, error ?? "unknown error");
            return false;
        }

        _reader = reader;
        CurrentTrack = track;
        _stalled = false;

        TrackChanged?.Invoke(this, track);
        ReadMetadataInBackground(track);
        return true;
    }

    /// <summary>
    /// Tags are read off the audio thread: TagLib opens and parses the file, which is far
    /// too slow to do while a buffer is waiting to be filled.
    /// </summary>
    private void ReadMetadataInBackground(TrackInfo track)
    {
        if (track.MetadataRead)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            TrackInfo enriched = _metadata.Read(track);
            if (!ReferenceEquals(CurrentTrack, track))
            {
                return;   // we already moved on
            }

            CurrentTrack = enriched;

            // Only re-announce when the tags actually told us something. Untagged files
            // (and files with nothing but a title we already derived) would otherwise
            // fire a second, identical TrackChanged for every track.
            if (enriched.Title is not null || enriched.Artist is not null || enriched.Duration is not null)
            {
                TrackChanged?.Invoke(this, enriched);
            }
        });
    }

    private void ReportStall(int failures)
    {
        if (_stalled)
        {
            return;
        }

        _stalled = true;
        string message = _queue.Count == 0
            ? "There are no playable files in your music folders."
            : $"Could not open any of the last {failures} tracks.";

        _log.LogError("Playback stalled: {Message}", message);
        Stalled?.Invoke(this, message);
    }

    private void CloseReader()
    {
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose() => CloseReader();
}
