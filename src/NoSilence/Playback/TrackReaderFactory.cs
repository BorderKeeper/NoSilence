using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NoSilence.Playback;

/// <summary>An open track: the decoded sample stream plus the file handle behind it.</summary>
internal sealed class TrackReader : IDisposable
{
    private readonly WaveStream _stream;

    internal TrackReader(TrackInfo track, WaveStream stream, ISampleProvider samples)
    {
        Track = track;
        _stream = stream;
        Samples = samples;
    }

    public TrackInfo Track { get; }

    public ISampleProvider Samples { get; }

    public TimeSpan Duration
    {
        get
        {
            try
            {
                return _stream.TotalTime;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                return TimeSpan.Zero;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            try
            {
                return _stream.CurrentTime;
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                return TimeSpan.Zero;
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>
/// Opens a file and adapts it to the pipeline's fixed format.
/// </summary>
/// <remarks>
/// The graph runs at one format for its whole life so the output device is never
/// reinitialised between tracks; every file is converted to meet it rather than the other
/// way round. <see cref="AudioFileReader"/> is tried first and Media Foundation second,
/// because the two cover different corners (Media Foundation handles m4a/aac/wma and, on
/// Windows 10+, FLAC; it cannot open Ogg or Opus at all).
/// </remarks>
internal sealed class TrackReaderFactory
{
    private readonly ILogger<TrackReaderFactory> _log;

    public TrackReaderFactory(ILogger<TrackReaderFactory> log) => _log = log;

    /// <summary>The single internal format: 32-bit float, 44.1 kHz, stereo.</summary>
    public static WaveFormat PipelineFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    public TrackReader? TryOpen(TrackInfo track, out string? error)
    {
        error = null;

        if (!File.Exists(track.Path))
        {
            error = "file no longer exists";
            return null;
        }

        WaveStream? stream = null;
        try
        {
            stream = OpenStream(track.Path, out error);
            if (stream is null)
            {
                return null;
            }

            ISampleProvider samples = Adapt(stream, track);
            return new TrackReader(track, stream, samples);
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            stream?.Dispose();
            error = ex.Message;
            _log.LogDebug(ex, "Could not open {Path}.", track.Path);
            return null;
        }
    }

    private WaveStream? OpenStream(string path, out string? error)
    {
        error = null;

        try
        {
            return new AudioFileReader(path);
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            _log.LogTrace(ex, "AudioFileReader could not open {Path}; trying Media Foundation.", path);
        }

        try
        {
            return new MediaFoundationReader(path);
        }
        catch (Exception ex) when (IsDecodeFailure(ex))
        {
            error = $"no decoder could open it ({ex.Message})";
            return null;
        }
    }

    private static ISampleProvider Adapt(WaveStream stream, TrackInfo track)
    {
        ISampleProvider samples = stream is ISampleProvider direct ? direct : stream.ToSampleProvider();

        samples = samples.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(samples),
            2 => samples,
            // Surround source: take the front pair rather than folding down, which needs a
            // proper matrix we have no reason to get into for background music.
            _ => Downmix(samples),
        };

        if (samples.WaveFormat.SampleRate != PipelineFormat.SampleRate)
        {
            // WDL's resampler is fully managed, so it works identically whether or not
            // Media Foundation is available.
            samples = new WdlResamplingSampleProvider(samples, PipelineFormat.SampleRate);
        }

        if (track.ReplayGainDb is { } gainDb)
        {
            samples = new VolumeSampleProvider(samples) { Volume = (float)Math.Pow(10d, gainDb / 20d) };
        }

        return samples;
    }

    private static ISampleProvider Downmix(ISampleProvider source)
    {
        var multiplexer = new MultiplexingSampleProvider([source], 2);
        multiplexer.ConnectInputToOutput(0, 0);
        multiplexer.ConnectInputToOutput(1, 1);
        return multiplexer;
    }

    /// <summary>
    /// The exception surface of "this file will not decode" is wide and inconsistent across
    /// NAudio's readers and Media Foundation, so it is enumerated once here.
    /// </summary>
    private static bool IsDecodeFailure(Exception ex) => ex is
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        InvalidOperationException or
        FormatException or
        ArgumentException or
        System.Runtime.InteropServices.COMException or
        System.Runtime.InteropServices.ExternalException or
        IndexOutOfRangeException;
}
