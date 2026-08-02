using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoSilence.Audio;
using NoSilence.Diagnostics;
using NoSilence.Playback;
using NoSilence.Settings;
using NoSilence.Ui;
using Serilog.Events;

namespace NoSilence.App;

/// <summary>
/// The single place that knows how the app is wired together. Keeping registration here
/// (rather than scattered <c>new</c> calls) is what lets the diagnostic commands build a
/// partial app — no tray, no playback — out of the same components.
/// </summary>
internal static class CompositionRoot
{
    public static ServiceProvider Build(CommandLineOptions options)
    {
        var paths = AppPaths.Resolve(options.DataRoot);
        ILoggerFactory loggerFactory = Logging.Build(
            paths,
            console: options.IsConsoleCommand,
            level: options.Verbose ? LogEventLevel.Debug : LogEventLevel.Information);

        var services = new ServiceCollection();

        services.AddSingleton(options);
        services.AddSingleton(paths);
        services.AddSingleton(loggerFactory);
        services.AddLogging();

        services.AddSingleton<DeviceCatalog>();
        services.AddSingleton<OutputDeviceResolver>();
        services.AddSingleton(sp => new AudioEngineThread(sp.GetRequiredService<ILogger<AudioEngineThread>>(), tickMs: 250));

        services.AddSingleton<SettingsService>();
        services.AddSingleton<MusicLibrary>();
        services.AddSingleton<MetadataReader>();
        services.AddSingleton<TrackReaderFactory>();
        services.AddSingleton<ShuffleQueue>(_ => new ShuffleQueue());
        services.AddSingleton<PlaylistSampleProvider>();
        services.AddSingleton<PlaybackEngine>();

        services.AddSingleton<ProcessInfoCache>();
        services.AddSingleton<AudioSessionProbe>();
        services.AddSingleton<Signals.SignalProbes>();
        services.AddSingleton<Detection.DetectionService>();
        services.AddSingleton<DiagnosticRunner>();

        services.AddSingleton<AppController>();
        services.AddSingleton<StartupRegistration>();
        services.AddSingleton<TrayApplicationContext>();
        services.AddSingleton<UiDispatcher>();
        services.AddSingleton<AppHost>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
