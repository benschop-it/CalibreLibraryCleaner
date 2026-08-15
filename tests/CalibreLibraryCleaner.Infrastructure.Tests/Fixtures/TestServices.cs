using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Infrastructure.Caches;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Pdf;
using Microsoft.Extensions.DependencyInjection;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;

internal static class TestServices
{
    public static ServiceProvider CreateProvider(string? formatHashCacheRoot = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        if (formatHashCacheRoot is null)
        {
            services.AddSingleton<IFormatHashCache, NullFormatHashCache>();
        }
        else
        {
            services.AddSingleton(new FormatHashCacheOptions(formatHashCacheRoot));
        }
        services.AddSingleton(new PdfWorkerOptions { ExecutablePath = FindPdfWorker() });
        services.AddSingleton(new LibraryAnalysisOptions());
        services.AddSingleton<EpubAssessmentEngine>();
        services.AddSingleton<AssessEpubFormatsUseCase>();
        services.AddSingleton<PdfPageSamplingPolicy>();
        services.AddSingleton<PdfClassificationPolicy>();
        services.AddSingleton<PdfAssessmentEngine>();
        services.AddSingleton<AssessPdfFormatsUseCase>();
        return services.BuildServiceProvider();
    }

    public static ScanLibraryUseCase CreateScanUseCase(ServiceProvider provider) => new(
        provider.GetRequiredService<ILibraryPathResolver>(),
        provider.GetRequiredService<ICalibreMetadataReader>(),
        provider.GetRequiredService<IFormatFileHasher>(),
        provider.GetRequiredService<IClock>(),
        provider.GetRequiredService<LibraryAnalysisOptions>(),
        provider.GetRequiredService<AssessEpubFormatsUseCase>(),
        assessPdfFormats: provider.GetRequiredService<AssessPdfFormatsUseCase>());

    private static string FindPdfWorker()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CalibreLibraryCleaner.sln")))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
        return Path.Combine(
            root,
            "src",
            "CalibreLibraryCleaner.PdfWorker",
            "bin",
            "Debug",
            "net10.0",
            OperatingSystem.IsWindows() ? "CalibreLibraryCleaner.PdfWorker.exe" : "CalibreLibraryCleaner.PdfWorker");
    }

    private sealed class NullFormatHashCache : IFormatHashCache
    {
        public Task<FormatHashCacheEntry?> TryReadAsync(
            FormatHashCacheKey key,
            CancellationToken cancellationToken) => Task.FromResult<FormatHashCacheEntry?>(null);

        public Task WriteAsync(FormatHashCacheEntry entry, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PruneAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
