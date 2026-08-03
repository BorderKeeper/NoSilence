using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NoSilence.App;
using NoSilence.Audio;
using NoSilence.Diagnostics;
using NoSilence.Interop;
using NoSilence.Ui;

namespace NoSilence;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitFailure = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        if (!CommandLineOptions.TryParse(args, out CommandLineOptions options, out string? parseError))
        {
            ConsoleAttach.EnsureConsole();
            Console.Error.WriteLine(parseError);
            Console.Error.WriteLine();
            Console.Error.WriteLine("Run NoSilence --help for usage.");
            ConsoleAttach.PauseIfOwnConsole();
            return ExitUsage;
        }

        if (options.Command == AppCommand.Help)
        {
            ConsoleAttach.EnsureConsole();
            Console.WriteLine(CommandLineOptions.UsageText);
            ConsoleAttach.PauseIfOwnConsole();
            return ExitOk;
        }

        if (options.IsConsoleCommand)
        {
            ConsoleAttach.EnsureConsole();
        }

        if (options.Command == AppCommand.Quit)
        {
            // Handled before the composition root so it does not touch settings, logs or
            // the audio stack of the instance it is trying to stop.
            bool sent = SingleInstance.RequestQuit();
            Console.WriteLine(sent ? "Asked NoSilence to shut down." : "Could not send the shutdown request.");
            ConsoleAttach.PauseIfOwnConsole();
            return sent ? ExitOk : ExitFailure;
        }

        ServiceProvider? services = null;
        try
        {
            services = CompositionRoot.Build(options);

            return options.Command switch
            {
                AppCommand.ListDevices => RunListDevices(services),
                AppCommand.WriteIcon => RunWriteIcon(options),
                AppCommand.Diagnose => services.GetRequiredService<DiagnosticRunner>().Run(options),
                AppCommand.Replay => RunReplay(services, options),
                AppCommand.DiscoverTv => RunDiscoverTv(services, options),
                AppCommand.WakeTv => RunTvPower(services, wake: true),
                AppCommand.SleepTv => RunTvPower(services, wake: false),
                _ => RunTray(services, options),
            };
        }
        catch (Exception ex)
        {
            ReportFatal(services, ex, options);
            return ExitFailure;
        }
        finally
        {
            services?.Dispose();
            Logging.Shutdown();

            if (options.IsConsoleCommand)
            {
                ConsoleAttach.PauseIfOwnConsole();
            }
        }
    }

    private static int RunTray(ServiceProvider services, CommandLineOptions options)
    {
        using SingleInstance? instance = SingleInstance.AcquireOrSignalExisting();
        if (instance is null)
        {
            // Another copy is already running and has been told to show its settings window.
            return ExitOk;
        }

        ApplicationConfiguration.Initialize();

        var log = services.GetRequiredService<ILogger<TrayApplicationContext>>();

        // A tray app must never die on an unhandled exception in a click handler, and it
        // must never show a modal crash dialog behind everything else.
        Application.ThreadException += (_, e) => log.LogError(e.Exception, "Unhandled UI exception.");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception; terminating: {Terminating}.", e.IsTerminating);

        var tray = services.GetRequiredService<TrayApplicationContext>();
        tray.SetState(TrayIconState.Waiting, "NoSilence — starting up");

        // Resolved here, on the UI thread, because UiDispatcher must build its marshalling
        // control on the thread that runs the message loop.
        using var host = services.GetRequiredService<AppHost>();
        host.Start(options.ResetSettings);

        if (options.ShowSettings)
        {
            host.ShowSettings();
        }

        Application.Run(tray);
        return ExitOk;
    }

    private static int RunListDevices(ServiceProvider services)
    {
        var catalog = services.GetRequiredService<DeviceCatalog>();

        // Include inactive endpoints: the whole point is to find the TV, which is very
        // likely switched off (and therefore absent) at the moment you go looking for it.
        const DeviceState AllInteresting = DeviceState.Active | DeviceState.Unplugged | DeviceState.NotPresent | DeviceState.Disabled;

        PrintEndpoints("Output devices (render)", catalog.List(DataFlow.Render, AllInteresting));
        Console.WriteLine();
        PrintEndpoints("Input devices (capture)", catalog.List(DataFlow.Capture, DeviceState.Active));

        Console.WriteLine();
        Console.WriteLine("The endpoint ID is what NoSilence stores, because it survives the TV being");
        Console.WriteLine("switched off and back on. Names and positions in this list do not.");
        return ExitOk;
    }

    private static void PrintEndpoints(string title, IReadOnlyList<AudioEndpointInfo> endpoints)
    {
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));

        if (endpoints.Count == 0)
        {
            Console.WriteLine("  (none)");
            return;
        }

        foreach (AudioEndpointInfo endpoint in endpoints)
        {
            string marker = endpoint.IsDefault ? "*" : " ";
            Console.WriteLine($"{marker} {endpoint.FriendlyName}");
            Console.WriteLine($"    state : {endpoint.DescribeState()}");
            Console.WriteLine($"    id    : {endpoint.Id}");
        }

        Console.WriteLine();
        Console.WriteLine("  * = current default device");
    }

    private static int RunWriteIcon(CommandLineOptions options)
    {
        string path = options.IconPath ?? Path.Combine(AppPaths.ExeDirectory, "NoSilence.ico");
        IconFactory.WriteIco(path, Color.FromArgb(0x1E, 0x29, 0x3B), Color.FromArgb(0x7A, 0xB0, 0xFF));
        Console.WriteLine($"Wrote {new FileInfo(path).Length:N0} bytes to {Path.GetFullPath(path)}");

        // Read it straight back: the ICO writer is hand-rolled, so proving the file parses
        // is worth more than trusting that it does.
        using (var roundTrip = new Icon(path, 32, 32))
        {
            Console.WriteLine($"Verified: reloaded a {roundTrip.Width}x{roundTrip.Height} entry.");
        }

        string preview = Path.ChangeExtension(path, ".preview.png");
        IconFactory.WritePreviewSheet(preview);
        Console.WriteLine($"Preview sheet: {Path.GetFullPath(preview)}");
        return ExitOk;
    }

    private static int RunReplay(ServiceProvider services, CommandLineOptions options)
    {
        string path = options.SnapshotPath!;
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No recording at {Path.GetFullPath(path)}.");
            return ExitFailure;
        }

        Settings.AppSettings settings = services.GetRequiredService<Settings.SettingsService>().Load();
        Detection.DetectionConfig config = settings.Detection;

        SnapshotReplayer.Result result = SnapshotReplayer.Run(SnapshotRecorder.Read(path), config);

        Console.WriteLine($"Replayed {result.Snapshots} snapshots covering {result.Duration:hh\\:mm\\:ss}");
        Console.WriteLine($"Settings: threshold {config.ThresholdDb:F0} dBFS · sustain {config.MinDurationMs} ms · release {config.ReleaseMs / 1000} s");
        Console.WriteLine();

        if (result.Transitions.Count == 0)
        {
            Console.WriteLine("  (the decision never changed)");
        }
        else
        {
            Console.WriteLine($"  {"at",10}  {"state",-8} reason");
            Console.WriteLine(new string('-', 78));
            foreach (SnapshotReplayer.Transition transition in result.Transitions)
            {
                Console.WriteLine($"  {transition.Elapsed:hh\\:mm\\:ss}  {(transition.Silent ? "SILENT" : "PLAYING"),-8} {transition.Reason}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Silent for {result.SilentFor:hh\\:mm\\:ss} ({result.SilentPercent:F0}% of the recording), {result.Transitions.Count} transitions.");

        if (result.WorstFlapIn30Seconds >= 3)
        {
            Console.WriteLine($"Worst flapping: {result.WorstFlapIn30Seconds} transitions inside 30 seconds — the threshold or a rule needs adjusting.");
        }

        return ExitOk;
    }

    private static int RunDiscoverTv(ServiceProvider services, CommandLineOptions options)
    {
        var discovery = services.GetRequiredService<Tv.Samsung.SamsungDiscovery>();

        IReadOnlyList<string> subnets = options.Subnet is { Length: > 0 }
            ? [options.Subnet]
            : Tv.Samsung.SamsungDiscovery.LocalSubnets();

        Console.WriteLine($"Sweeping {string.Join(", ", subnets.Select(s => s + ".1-254"))} for Samsung televisions…");
        Console.WriteLine();

        IReadOnlyList<Tv.Samsung.SamsungDeviceInfo> found = discovery
            .SweepAsync(options.Subnet, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        if (found.Count == 0)
        {
            Console.WriteLine("No televisions answered.");
            Console.WriteLine();
            Console.WriteLine("A Samsung set only answers when network standby is enabled:");
            Console.WriteLine("  Settings > General > Network > Expert Settings > Power On with Mobile");
            return ExitFailure;
        }

        foreach (Tv.Samsung.SamsungDeviceInfo tv in found)
        {
            Console.WriteLine(tv.Describe());
            Console.WriteLine($"    ip          : {tv.Ip}");
            Console.WriteLine($"    power       : {tv.PowerState ?? "not reported by this firmware"}");
            Console.WriteLine($"    mac (its own): {tv.Mac ?? "not reported"}");

            // The set's own answer is frequently its Wi-Fi radio, which cannot wake a wired
            // one; ARP reports the adapter actually carrying this address.
            if (System.Net.IPAddress.TryParse(tv.Ip, out System.Net.IPAddress? address) &&
                Tv.WakeOnLan.TryResolveMacViaArp(address) is { } arp)
            {
                Console.WriteLine($"    mac (ARP)   : {Tv.WakeOnLan.FormatMac(arp)}   <- prefer this one");
            }

            Console.WriteLine();
        }

        Console.WriteLine("Set these on the Television tab in Settings.");
        return ExitOk;
    }

    /// <summary>
    /// Drives the television directly, without the tray. Useful for scripting, and the only
    /// way to exercise the wake path without clicking a menu.
    /// </summary>
    private static int RunTvPower(ServiceProvider services, bool wake)
    {
        var settingsService = services.GetRequiredService<Settings.SettingsService>();
        var stateService = services.GetRequiredService<Settings.StateService>();
        var tv = services.GetRequiredService<Tv.TvService>();
        var catalog = services.GetRequiredService<DeviceCatalog>();

        Settings.AppSettings settings = settingsService.Load();
        stateService.Load();

        if (string.Equals(settings.Tv.Provider, "none", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("No television is configured. Use --discover-tv, or the Television tab in Settings.");
            return ExitFailure;
        }

        // "Is the television on?" is answered by whether its audio endpoint is active — no
        // network round trip, and it is the only signal that also means "on this input".
        bool EndpointPresent()
        {
            string? id = settings.Output.DeviceId;
            return id is not null && catalog.List(DataFlow.Render).Any(d => string.Equals(d.Id, id, StringComparison.Ordinal));
        }

        tv.Diagnostic += (_, e) => Console.WriteLine($"  {(e.IsError ? "!" : "-")} {e.Message}");
        tv.Configure(EndpointPresent, () => true, () => true, () => Detection.OverrideState.Auto);

        Console.WriteLine($"{tv.Controller.DisplayName}");
        Console.WriteLine($"HDMI audio endpoint present: {EndpointPresent()}");
        Console.WriteLine();

        bool result = wake
            ? tv.Controller.WakeAsync(CancellationToken.None).GetAwaiter().GetResult()
            : tv.Controller.SleepAsync(CancellationToken.None).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine(result ? "Done." : "That did not work.");
        Console.WriteLine($"HDMI audio endpoint present now: {EndpointPresent()}");

        stateService.Save();
        return result ? ExitOk : ExitFailure;
    }

    private static int RunNotYetImplemented(CommandLineOptions options)
    {
        Console.Error.WriteLine($"--{options.Command.ToString().ToLowerInvariant()} is not implemented yet.");
        return ExitFailure;
    }

    private static void ReportFatal(ServiceProvider? services, Exception ex, CommandLineOptions options)
    {
        try
        {
            services?.GetService<ILoggerFactory>()?.CreateLogger("NoSilence").LogCritical(ex, "Fatal error during startup.");
        }
        catch (ObjectDisposedException)
        {
            // Logging is already gone; the console message below is what matters.
        }

        if (options.IsConsoleCommand)
        {
            Console.Error.WriteLine(ex);
        }
        else
        {
            MessageBox.Show(
                $"NoSilence could not start.\n\n{ex.Message}",
                "NoSilence",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
