using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NoSilence.Settings;

namespace NoSilence.Playback;

/// <summary>
/// Scans the configured folders and keeps the track list current.
/// </summary>
/// <remarks>
/// Scanning happens off the audio thread and off the UI thread; the result is published as
/// an immutable list so nothing has to lock to read it. Files that turn out to be
/// unreadable are remembered so we stop retrying them every cycle, but the list is exposed
/// so the user can see what was skipped and why.
/// </remarks>
internal sealed class MusicLibrary : IDisposable
{
    private readonly ILogger<MusicLibrary> _log;
    private readonly ConcurrentDictionary<string, string> _skipped = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _rescanGate = new();

    private System.Threading.Timer? _debounce;
    private LibrarySettings _settings = new();
    private bool _disposed;

    public MusicLibrary(ILogger<MusicLibrary> log) => _log = log;

    /// <summary>The current track set. Replaced wholesale on rescan; safe to read from any thread.</summary>
    public IReadOnlyList<TrackInfo> Tracks { get; private set; } = [];

    /// <summary>Files we failed to open, mapped to the reason. Shown in the Library tab.</summary>
    public IReadOnlyDictionary<string, string> Skipped => _skipped;

    /// <summary>Raised after a rescan changed the track set. Fires on a background thread.</summary>
    public event EventHandler? Changed;

    public void Configure(LibrarySettings settings)
    {
        _settings = settings;
        Rescan();
        StartWatching();
    }

    public void Rescan()
    {
        lock (_rescanGate)
        {
            var extensions = new HashSet<string>(_settings.Extensions, StringComparer.OrdinalIgnoreCase);
            var found = new List<TrackInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string folder in _settings.Folders)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    _log.LogWarning("Library folder {Folder} does not exist; skipping it.", folder);
                    continue;
                }

                ScanFolder(folder, extensions, found, seen);
            }

            found.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

            bool changed = found.Count != Tracks.Count
                || !found.Select(t => t.Path).SequenceEqual(Tracks.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);

            Tracks = found;
            _log.LogInformation("Library scan found {Count} tracks across {Folders} folder(s).", found.Count, _settings.Folders.Count);

            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void ScanFolder(string folder, HashSet<string> extensions, List<TrackInfo> found, HashSet<string> seen)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,       // a permission-denied subfolder must not abort the scan
            AttributesToSkip = FileAttributes.System | FileAttributes.Temporary,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        try
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*", options))
            {
                if (!extensions.Contains(Path.GetExtension(path)) || _skipped.ContainsKey(path) || !seen.Add(path))
                {
                    continue;
                }

                try
                {
                    found.Add(TrackInfo.FromFile(new FileInfo(path)));
                }
                catch (IOException ex)
                {
                    _log.LogDebug(ex, "Could not stat {Path}.", path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Scan of {Folder} stopped early.", folder);
        }
    }

    /// <summary>Records a file we could not decode, so the playlist stops trying it every cycle.</summary>
    public void MarkUnreadable(string path, string reason)
    {
        if (_skipped.TryAdd(path, reason))
        {
            _log.LogWarning("Skipping {Path}: {Reason}", path, reason);
        }
    }

    /// <summary>Clears the skip list and rescans — the "Retry all" button.</summary>
    public void RetrySkipped()
    {
        _skipped.Clear();
        Rescan();
    }

    private void StartWatching()
    {
        StopWatching();

        foreach (string folder in _settings.Folders.Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    // The default 8 KB buffer overflows easily on a big folder tree, and an
                    // overflow silently drops events.
                    InternalBufferSize = 64 * 1024,
                };

                watcher.Created += OnFileSystemChanged;
                watcher.Deleted += OnFileSystemChanged;
                watcher.Renamed += OnFileSystemChanged;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;

                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            {
                _log.LogWarning(ex, "Could not watch {Folder} for changes; you will need to rescan manually.", folder);
            }
        }
    }

    private void StopWatching()
    {
        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    /// <summary>
    /// Copying a folder in fires thousands of events; a debounce turns that into one rescan.
    /// </summary>
    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        _debounce ??= new System.Threading.Timer(_ => SafeRescan(), null, Timeout.Infinite, Timeout.Infinite);
        _debounce.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow: events were dropped, so the only safe response is a full rescan.
        _log.LogWarning(e.GetException(), "File system watcher error; forcing a full rescan.");
        _debounce?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    private void SafeRescan()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Rescan();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Automatic rescan failed.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopWatching();
        _debounce?.Dispose();
    }
}
