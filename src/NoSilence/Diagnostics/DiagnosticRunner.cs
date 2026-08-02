using Microsoft.Extensions.Logging;
using NoSilence.Audio;
using NoSilence.Detection;
using NoSilence.Settings;

namespace NoSilence.Diagnostics;

/// <summary>
/// The headless <c>--diagnose</c> loop: shows what every application is playing, live, and
/// optionally records it for replay.
/// </summary>
/// <remarks>
/// Built in M3 rather than bolted on later, because it is the tool used to tune everything
/// that comes after. Watching this table while a Discord ping arrives tells you in seconds
/// what an afternoon of guessing at thresholds will not.
/// </remarks>
internal sealed class DiagnosticRunner
{
    private readonly DetectionService _detection;
    private readonly SettingsService _settings;
    private readonly AudioEngineThread _engine;
    private readonly Playback.MusicLibrary _library;
    private readonly Playback.PlaybackEngine _playback;
    private readonly ILogger<DiagnosticRunner> _log;

    public DiagnosticRunner(
        DetectionService detection,
        SettingsService settings,
        AudioEngineThread engine,
        Playback.MusicLibrary library,
        Playback.PlaybackEngine playback,
        ILogger<DiagnosticRunner> log)
    {
        _detection = detection;
        _settings = settings;
        _engine = engine;
        _library = library;
        _playback = playback;
        _log = log;
    }

    public int Run(CommandLineOptions options)
    {
        AppSettings settings = _settings.Load();
        DetectionConfig config = settings.Detection;
        _detection.Configure(config);

        using SnapshotRecorder? recorder = options.SnapshotPath is { } path ? new SnapshotRecorder(path) : null;

        Console.WriteLine("NoSilence detection diagnostics");
        Console.WriteLine($"threshold {config.ThresholdDb:F0} dBFS · sustain {config.MinDurationMs} ms · release {config.ReleaseMs / 1000} s · tick {config.PollIntervalMs} ms");
        if (recorder is not null)
        {
            Console.WriteLine($"recording to {recorder.Path_}");

            if (recorder.PreservedPreviousAs is { } preserved)
            {
                Console.WriteLine($"kept the previous recording as {preserved}");
            }
        }

        if (options.PlayWhileDiagnosing)
        {
            Console.WriteLine("playing music as well, so you can hear the decisions being made");
        }

        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        using var stop = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };

        _engine.Start();

        if (options.PlayWhileDiagnosing)
        {
            _library.Configure(settings.Library);
            _playback.Start();
            _engine.Tick += _playback.Poll;
            _playback.Configure(settings);
        }

        DateTimeOffset started = DateTimeOffset.Now;
        var state = new DecisionState();
        int ticks = 0;

        while (!stop.IsSet)
        {
            if (options.Duration is { } limit && DateTimeOffset.Now - started >= limit)
            {
                break;
            }

            // Capture and evaluate on the engine thread: WASAPI session enumeration has to
            // happen on the MTA thread that owns it.
            (DetectionSnapshot snapshot, DecisionOutcome outcome) = _engine.Invoke(() =>
            {
                DetectionSnapshot captured = _detection.Capture();
                return (captured, DecisionEngine.Evaluate(captured, config, state));
            });

            recorder?.Write(snapshot);
            ticks++;

            if (options.PlayWhileDiagnosing)
            {
                _playback.ApplyDecision(outcome);
            }

            Render(snapshot, outcome);

            stop.Wait(config.PollIntervalMs);
        }

        if (options.PlayWhileDiagnosing)
        {
            _engine.Tick -= _playback.Poll;
            _playback.Dispose();
            _library.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine($"Stopped after {DateTimeOffset.Now - started:hh\\:mm\\:ss}.");

        if (recorder is not null)
        {
            recorder.Dispose();
            Console.WriteLine($"Wrote {recorder.Count} snapshots to {recorder.Path_}");
            Console.WriteLine($"Replay it with:  NoSilence --replay \"{recorder.Path_}\"");
        }

        return 0;
    }

    private string _lastSignature = string.Empty;
    private DateTimeOffset _lastAppendAt;

    private void Render(DetectionSnapshot snapshot, DecisionOutcome outcome)
    {
        // Redraw in place when we own the console; fall back to appending when redirected.
        bool canRedraw = !Console.IsOutputRedirected;

        if (!canRedraw)
        {
            // Piped or redirected: reprinting the whole table four times a second would be
            // 1,200 frames over a five-minute run. Print only when something actually
            // changed, with a slow heartbeat so the file still shows it was alive.
            string signature = outcome.WantsSilence + "|" + outcome.Reason + "|" +
                string.Join(",", outcome.Contributions.Where(c => c.Counts).Select(c => c.Source));

            bool changed = !string.Equals(signature, _lastSignature, StringComparison.Ordinal);
            bool heartbeat = snapshot.At - _lastAppendAt >= TimeSpan.FromSeconds(10);

            if (!changed && !heartbeat)
            {
                return;
            }

            _lastSignature = signature;
            _lastAppendAt = snapshot.At;
        }

        if (canRedraw)
        {
            try
            {
                Console.SetCursorPosition(0, Math.Min(Console.CursorTop, 6));
            }
            catch (IOException)
            {
                canRedraw = false;
            }
        }

        var lines = new List<string>
        {
            $"  {snapshot.At:HH:mm:ss}   {(outcome.WantsSilence ? "SILENT " : "PLAYING")}   {outcome.Reason}",
            new('-', 92),
            $"  {"process",-26} {"endpoint",-22} {"dBFS",8} {"sustained",10} {"rule",-12} counts",
        };

        IEnumerable<TriggerContribution> rows = outcome.Contributions
            .OrderByDescending(c => c.Counts)
            .ThenByDescending(c => c.Dbfs ?? double.MinValue);

        foreach (TriggerContribution row in rows.Take(18))
        {
            string db = row.Dbfs is { } value ? $"{value,8:F1}" : new string(' ', 8);
            string sustained = row.SustainedMs > 0 ? $"{row.SustainedMs / 1000d,9:F1}s" : new string(' ', 10);
            string endpoint = Truncate(row.Endpoint ?? string.Empty, 22);

            lines.Add($"  {Truncate(row.Source, 26),-26} {endpoint,-22} {db} {sustained} {Truncate(row.Rule ?? "-", 12),-12} {(row.Counts ? "YES" : "no")}");
        }

        if (snapshot.DefaultEndpointMuted)
        {
            lines.Add("  (default endpoint is muted, so nothing on it counts)");
        }

        foreach (string line in lines)
        {
            Console.WriteLine(canRedraw ? line.PadRight(Math.Max(0, Console.WindowWidth - 1)) : line);
        }

        if (canRedraw)
        {
            // Wipe whatever the previous, longer frame left behind.
            for (int i = 0; i < 4; i++)
            {
                Console.WriteLine(new string(' ', Math.Max(0, Console.WindowWidth - 1)));
            }

            Console.SetCursorPosition(0, Math.Max(0, Console.CursorTop - lines.Count - 4));
        }
    }

    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : string.Concat(value.AsSpan(0, width - 1), "…");
}
