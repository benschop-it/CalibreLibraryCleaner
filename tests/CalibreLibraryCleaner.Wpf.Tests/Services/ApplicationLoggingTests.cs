using System.IO;
using CalibreLibraryCleaner.Wpf.Services;
using FluentAssertions;
using Serilog;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.Services;

public sealed class ApplicationLoggingTests
{
    [Fact]
    public void DefaultLoggingSuppressesAssessmentDebugDetails()
    {
        using TemporaryLogDirectory directory = new();
        WriteMessages(directory.Path, diagnosticLogging: false);

        string output = directory.ReadAll();
        output.Should().Contain("aggregate-information");
        output.Should().NotContain("chapter-debug-detail");
    }

    [Fact]
    public void ExplicitDiagnosticLoggingEnablesAssessmentDebugDetails()
    {
        using TemporaryLogDirectory directory = new();
        WriteMessages(directory.Path, diagnosticLogging: true);

        directory.ReadAll().Should().Contain("chapter-debug-detail");
    }

    private static void WriteMessages(string directory, bool diagnosticLogging)
    {
        ILogger root = ApplicationLogging.CreateLogger(directory, diagnosticLogging);
        try
        {
            ILogger logger = root.ForContext(
                "SourceContext", "CalibreLibraryCleaner.Application.Assessments.Test");
            logger.Debug("chapter-debug-detail");
            logger.Information("aggregate-information");
        }
        finally
        {
            (root as IDisposable)?.Dispose();
        }
    }

    private sealed class TemporaryLogDirectory : IDisposable
    {
        public TemporaryLogDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"calibre-logging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string ReadAll() => string.Concat(
            Directory.GetFiles(Path, "*.log").Select(File.ReadAllText));

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
