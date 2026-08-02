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
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = false };
        Path_ = Path.GetFullPath(path);
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
