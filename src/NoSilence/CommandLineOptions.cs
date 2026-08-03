using System.Globalization;

namespace NoSilence;

internal enum AppCommand
{
    /// <summary>Normal operation: tray icon, no console.</summary>
    Tray,

    /// <summary>Print the WASAPI render endpoints with their IDs and states, then exit.</summary>
    ListDevices,

    /// <summary>Headless detection loop that prints and optionally records snapshots.</summary>
    Diagnose,

    /// <summary>Re-run recorded snapshots through the current decision engine.</summary>
    Replay,

    /// <summary>Sweep the LAN for Samsung TVs, then exit.</summary>
    DiscoverTv,

    /// <summary>Turn the configured television on, then exit.</summary>
    WakeTv,

    /// <summary>Turn the configured television off, then exit.</summary>
    SleepTv,

    /// <summary>Regenerate the application icon asset, then exit. Design-time only.</summary>
    WriteIcon,

    /// <summary>Ask a running instance to shut down cleanly, then exit.</summary>
    Quit,

    /// <summary>Print usage, then exit.</summary>
    Help,
}

/// <summary>
/// Hand-rolled argument parsing — the surface is small enough that a dependency would
/// cost more than it saves.
/// </summary>
/// <remarks>
/// v1 warned about a wrong argument count and then carried on to crash with
/// <c>IndexOutOfRangeException</c>. Here a bad argument is a hard, explained failure.
/// </remarks>
internal sealed record CommandLineOptions
{
    public AppCommand Command { get; init; } = AppCommand.Tray;

    /// <summary>Override for the settings/state/log root. Mostly for testing.</summary>
    public string? DataRoot { get; init; }

    /// <summary>How long <c>--diagnose</c> runs. Null means until Ctrl+C.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>JSONL snapshot recording target for <c>--diagnose</c>, or the input for <c>--replay</c>.</summary>
    public string? SnapshotPath { get; init; }

    /// <summary>Also play music during <c>--diagnose</c> rather than only observing.</summary>
    public bool PlayWhileDiagnosing { get; init; }

    /// <summary>Subnet prefix for <c>--discover-tv</c>, e.g. <c>192.168.1</c>. Null means auto-detect.</summary>
    public string? Subnet { get; init; }

    /// <summary>Output path for <c>--write-icon</c>.</summary>
    public string? IconPath { get; init; }

    /// <summary>Start with the settings window open.</summary>
    public bool ShowSettings { get; init; }

    /// <summary>Discard settings.json and start from defaults.</summary>
    public bool ResetSettings { get; init; }

    /// <summary>Log at debug level — shows every decision the device state machine makes.</summary>
    public bool Verbose { get; init; }

    /// <summary>True for the commands that write to stdout and need a console attached.</summary>
    public bool IsConsoleCommand => Command is AppCommand.ListDevices or AppCommand.Diagnose
        or AppCommand.Replay or AppCommand.DiscoverTv or AppCommand.WriteIcon or AppCommand.Help
        or AppCommand.Quit or AppCommand.WakeTv or AppCommand.SleepTv;

    public static bool TryParse(string[] args, out CommandLineOptions options, out string? error)
    {
        var command = AppCommand.Tray;
        string? dataRoot = null;
        string? snapshotPath = null;
        string? subnet = null;
        string? iconPath = null;
        TimeSpan? duration = null;
        bool play = false;
        bool showSettings = false;
        bool resetSettings = false;
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            // Accept /flag and -flag as well as --flag; people type all three.
            string name = arg.TrimStart('-', '/').ToLowerInvariant();

            switch (name)
            {
                case "help" or "h" or "?":
                    command = AppCommand.Help;
                    break;

                case "list-devices" or "listdevices" or "devices":
                    command = AppCommand.ListDevices;
                    break;

                case "diagnose":
                    command = AppCommand.Diagnose;
                    break;

                case "discover-tv" or "discovertv":
                    command = AppCommand.DiscoverTv;
                    break;

                case "wake-tv" or "waketv":
                    command = AppCommand.WakeTv;
                    break;

                case "sleep-tv" or "sleeptv":
                    command = AppCommand.SleepTv;
                    break;

                case "tray":
                    command = AppCommand.Tray;
                    break;

                case "quit" or "exit":
                    command = AppCommand.Quit;
                    break;

                case "play":
                    play = true;
                    break;

                case "settings":
                    showSettings = true;
                    break;

                case "reset-settings":
                    resetSettings = true;
                    break;

                case "verbose" or "v":
                    verbose = true;
                    break;

                case "replay":
                    command = AppCommand.Replay;
                    if (!TryTakeValue(args, ref i, out snapshotPath, out error))
                    {
                        options = new CommandLineOptions();
                        return false;
                    }

                    break;

                case "write-icon":
                    command = AppCommand.WriteIcon;
                    if (!TryTakeValue(args, ref i, out iconPath, out error))
                    {
                        options = new CommandLineOptions();
                        return false;
                    }

                    break;

                case "jsonl":
                    if (!TryTakeValue(args, ref i, out snapshotPath, out error))
                    {
                        options = new CommandLineOptions();
                        return false;
                    }

                    break;

                case "data-root":
                    if (!TryTakeValue(args, ref i, out dataRoot, out error))
                    {
                        options = new CommandLineOptions();
                        return false;
                    }

                    break;

                case "subnet":
                    if (!TryTakeValue(args, ref i, out subnet, out error))
                    {
                        options = new CommandLineOptions();
                        return false;
                    }

                    break;

                case "seconds":
                    if (!TryTakeValue(args, ref i, out string? rawSeconds, out error))
                    {
                        options = new CommandLineOptions();
                        return false;
                    }

                    if (!double.TryParse(rawSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds <= 0)
                    {
                        options = new CommandLineOptions();
                        error = $"--seconds expects a positive number, got '{rawSeconds}'.";
                        return false;
                    }

                    duration = TimeSpan.FromSeconds(seconds);
                    break;

                default:
                    options = new CommandLineOptions();
                    error = $"Unknown argument '{arg}'. Run with --help to see what is supported.";
                    return false;
            }
        }

        if (command == AppCommand.Replay && string.IsNullOrWhiteSpace(snapshotPath))
        {
            options = new CommandLineOptions();
            error = "--replay expects the path to a recording made with --diagnose --jsonl.";
            return false;
        }

        options = new CommandLineOptions
        {
            Command = command,
            DataRoot = dataRoot,
            Duration = duration,
            SnapshotPath = snapshotPath,
            PlayWhileDiagnosing = play,
            Subnet = subnet,
            IconPath = iconPath,
            ShowSettings = showSettings,
            ResetSettings = resetSettings,
            Verbose = verbose,
        };
        error = null;
        return true;
    }

    private static bool TryTakeValue(string[] args, ref int i, out string? value, out string? error)
    {
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            error = $"{args[i]} expects a value.";
            return false;
        }

        value = args[++i];
        error = null;
        return true;
    }

    public static string UsageText => """
        NoSilence — plays music on a secondary output whenever your PC is otherwise silent.

        Run with no arguments to start in the system tray. Everything is configured from
        there; there is no configuration to pass on the command line.

          --settings              Start with the settings window open.
          --reset-settings        Discard settings.json and start from defaults.
          --list-devices          Print the audio output devices with their endpoint IDs.
          --quit                  Ask a running instance to shut down cleanly.

        Tuning the silence detection:
          --diagnose              Watch what every application is playing, live, in the console.
            --seconds N             Stop after N seconds (default: until Ctrl+C).
            --jsonl FILE            Record one snapshot per tick to FILE for later replay.
            --play                  Also play music while diagnosing.
          --replay FILE           Re-run a recording through the current settings and show
                                  where the play/silence decision would flip. Change a
                                  threshold, replay, compare — no need to relaunch the game.

        Television:
          --discover-tv           Sweep the local network for Samsung TVs.
            --subnet 192.168.1      Restrict the sweep (default: auto-detect).
          --wake-tv               Turn the configured television on and report what happened.
          --sleep-tv              Turn the configured television off.

        Other:
          --data-root DIR         Use DIR instead of %APPDATA%\NoSilence.
          --verbose               Log at debug level. Shows every decision the output
                                  device state machine makes - useful when the TV is not
                                  being picked up.
          --help                  This text.
        """;
}
