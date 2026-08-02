using System.Text.Json;
using NoSilence.Detection;
using NoSilence.Settings;

namespace NoSilence.Diagnostics;

/// <summary>
/// Writes one <see cref="DetectionSnapshot"/> per line as JSON.
/// </summary>
/// <remarks>
/// JSONL rather than a single JSON document so a recording is still usable if the app is
/// killed mid-session, and so it can be tailed while running.
/// </remarks>
internal sealed class SnapshotRecorder : IDisposable
{
    private readonly StreamWriter _writer;
    private bool _disposed;

    public SnapshotRecorder(string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        PreservedPreviousAs = PreserveExisting(full);

        _writer = new StreamWriter(full, append: false) { AutoFlush = false };
        Path_ = full;
    }

    /// <summary>Where an existing recording was moved to, if one was in the way.</summary>
    public string? PreservedPreviousAs { get; }

    /// <summary>
    /// Moves an existing recording aside rather than truncating it.
    /// </summary>
    /// <remarks>
    /// Recordings are expensive to produce — they need somebody to sit and actually play a
    /// game or take a call — and re-running the same command is the most natural thing in
    /// the world. Silently overwriting one has already cost a session's worth of data once.
    /// </remarks>
    private static string? PreserveExisting(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return null;
        }

        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string stamp = File.GetLastWriteTime(path).ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

        string target = Path.Combine(directory, $"{name}-{stamp}{extension}");
        for (int i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(directory, $"{name}-{stamp}-{i}{extension}");
        }

        try
        {
            File.Move(path, target);
            return target;
        }
        catch (IOException)
        {
            // Locked by something else; carry on rather than refusing to record.
            return null;
        }
    }

    public string Path_ { get; }

    public int Count { get; private set; }

    public void Write(DetectionSnapshot snapshot)
    {
        if (_disposed)
        {
            return;
        }

        _writer.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions.Compact));
        Count++;

        // Flushing every few seconds keeps a Ctrl+C from losing the tail without paying for
        // a flush on every single tick.
        if (Count % 20 == 0)
        {
            _writer.Flush();
        }
    }

    public static IEnumerable<DetectionSnapshot> Read(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            DetectionSnapshot? snapshot = null;
            try
            {
                snapshot = JsonSerializer.Deserialize<DetectionSnapshot>(line, JsonOptions.Compact);
            }
            catch (JsonException)
            {
                // A truncated final line is expected if the recording was interrupted.
            }

            if (snapshot is not null)
            {
                yield return snapshot;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer.Flush();
        _writer.Dispose();
    }
}
