using Microsoft.Extensions.Logging;
using NoSilence.App;
using Serilog;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace NoSilence.Diagnostics;

/// <summary>
/// Serilog bootstrap. A rolling file in the data directory is the only sink in tray mode;
/// the console sink is added for the diagnostic commands, which are meant to be watched.
/// </summary>
internal static class Logging
{
    private static Serilog.Core.Logger? _serilog;

    public static ILoggerFactory Build(AppPaths paths, bool console, LogEventLevel level = LogEventLevel.Information)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: paths.LogFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: false,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        if (console)
        {
            config = config.WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        _serilog = config.CreateLogger();
        Log.Logger = _serilog;

        return LoggerFactory.Create(builder => builder.AddSerilog(_serilog, dispose: false));
    }

    public static void Shutdown()
    {
        _serilog?.Dispose();
        _serilog = null;
    }

    /// <summary>A logger for use before <see cref="Build"/> has run, or if it throws.</summary>
    public static ILogger Null => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
}
