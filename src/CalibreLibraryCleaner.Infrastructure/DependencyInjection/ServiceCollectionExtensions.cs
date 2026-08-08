using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.Epub;
using CalibreLibraryCleaner.Infrastructure.Execution;
using CalibreLibraryCleaner.Infrastructure.Hashing;
using CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;
using CalibreLibraryCleaner.Infrastructure.Paths;
using CalibreLibraryCleaner.Infrastructure.Pdf;
using CalibreLibraryCleaner.Infrastructure.Recommendations;
using CalibreLibraryCleaner.Infrastructure.Sqlite;
using CalibreLibraryCleaner.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;

namespace CalibreLibraryCleaner.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCalibreLibraryInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Enable legacy single-byte code pages (e.g. windows-1252) so EPUB XML
        // documents that declare a non-Unicode encoding decode accurately. This
        // provider is process-global and registering it more than once is safe.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ILibraryPathResolver, LibraryPathResolver>();
        services.AddSingleton<ICalibreMetadataReader, SqliteCalibreMetadataReader>();
        services.AddSingleton<IFormatFileHasher, StreamingSha256FormatFileHasher>();
        services.AddSingleton(new LibrarySnapshotStorageOptions());
        services.AddSingleton<ILibrarySnapshotStore, VersionedJsonLibrarySnapshotStore>();
        services.AddSingleton<ILibraryStateStore, VersionedJsonLibraryStateStore>();
        services.AddSingleton<VersOneEpubInspector>();
        services.AddSingleton<IEpubInspector>(provider => provider.GetRequiredService<VersOneEpubInspector>());
        services.AddSingleton<IEpubContentSignatureInspector>(provider => provider.GetRequiredService<VersOneEpubInspector>());
        services.AddSingleton(new EpubContentSignatureCacheOptions());
        services.AddSingleton<IEpubContentSignatureCache, FileEpubContentSignatureCache>();
        services.AddSingleton(new PdfWorkerOptions());
        services.AddSingleton<IPdfInspector, IsolatedPdfInspector>();
        services.AddSingleton<IRecommendationExporter, VersionedJsonRecommendationExporter>();
        services.AddSingleton(new CalibreExecutionOptions());
        services.AddSingleton(new ExecutionStorageOptions());
        services.AddSingleton<DirectCalibreProcessRunner>();
        services.AddSingleton<ICalibreToolDiscovery, CalibreToolDiscovery>();
        services.AddSingleton<IEbookViewerLauncher, CalibreEbookViewerLauncher>();
        services.AddSingleton<ICalibreMutationWorkerFactory, PersistentCalibreMutationWorkerFactory>();
        services.AddSingleton<ILibraryMutationLease, FileLibraryMutationLease>();
        services.AddSingleton<ICleanupExecutionIdGenerator, SystemCleanupExecutionIdGenerator>();
        return services;
    }
}
