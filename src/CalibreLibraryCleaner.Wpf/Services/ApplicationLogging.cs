using System.Globalization;
using System.IO;
using Serilog;
using Serilog.Events;

namespace CalibreLibraryCleaner.Wpf.Services;

internal static class ApplicationLogging
{
    public const int RetainedFileCountLimit = 20;
    public const long FileSizeLimitBytes = 25L * 1024 * 1024;
    public static TimeSpan FlushInterval { get; } = TimeSpan.FromSeconds(1);

    public static string DefaultLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CalibreLibraryCleaner",
        "logs");

    public static string GetLogFilePattern(string logDirectory) =>
        Path.Combine(Path.GetFullPath(logDirectory), "calibre-library-cleaner-.log");

    public static Serilog.ILogger CreateLogger(string? logDirectory = null)
    {
        string directory = Path.GetFullPath(logDirectory ?? DefaultLogDirectory);
        Directory.CreateDirectory(directory);
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("CalibreLibraryCleaner.Application.Assessments", LogEventLevel.Debug)
            .MinimumLevel.Override("CalibreLibraryCleaner.Application.Libraries", LogEventLevel.Debug)
            .MinimumLevel.Override("CalibreLibraryCleaner.Application.Matching", LogEventLevel.Debug)
            .MinimumLevel.Override("CalibreLibraryCleaner.Infrastructure.Epub", LogEventLevel.Debug)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "CalibreLibraryCleaner")
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.File(
                GetLogFilePattern(directory),
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] [EventId={EventId}] {Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: FileSizeLimitBytes,
                retainedFileCountLimit: RetainedFileCountLimit,
                buffered: true,
                flushToDiskInterval: FlushInterval,
                shared: false)
            .CreateLogger();
    }
}
