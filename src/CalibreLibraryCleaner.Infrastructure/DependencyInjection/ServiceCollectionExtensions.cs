using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.Epub;
using CalibreLibraryCleaner.Infrastructure.Execution;
using CalibreLibraryCleaner.Infrastructure.Hashing;
using CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;
using CalibreLibraryCleaner.Infrastructure.Paths;
using CalibreLibraryCleaner.Infrastructure.Pdf;
using CalibreLibraryCleaner.Infrastructure.Plans;
using CalibreLibraryCleaner.Infrastructure.Recommendations;
using CalibreLibraryCleaner.Infrastructure.Recovery;
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
        services.AddSingleton<IEpubInspector, VersOneEpubInspector>();
        services.AddSingleton(new PdfWorkerOptions());
        services.AddSingleton<IPdfInspector, IsolatedPdfInspector>();
        services.AddSingleton<IRecommendationExporter, VersionedJsonRecommendationExporter>();
        services.AddSingleton<ICleanupPlanIdGenerator, SystemCleanupPlanIdGenerator>();
        services.AddSingleton<ICleanupPlanStore, VersionedJsonCleanupPlanStore>();
        services.AddSingleton(new CalibreExecutionOptions());
        services.AddSingleton(new ExecutionStorageOptions());
        services.AddSingleton<DirectCalibreProcessRunner>();
        services.AddSingleton<ICalibreToolDiscovery, CalibreToolDiscovery>();
        services.AddSingleton<CalibreCommandGateway>();
        services.AddSingleton<ICalibreCommandGateway>(provider =>
            provider.GetRequiredService<CalibreCommandGateway>());
        services.AddSingleton<IRecoveryCalibreGateway>(provider =>
            provider.GetRequiredService<CalibreCommandGateway>());
        services.AddSingleton<ICalibreExecutionProfileProvider, CalibreRecoveryExecutionProfileProvider>();
        services.AddSingleton<ILibraryMutationLease, FileLibraryMutationLease>();
        services.AddSingleton<IExecutionBackupStore, FileExecutionBackupStore>();
        services.AddSingleton<IExactBinaryRecordBackupStore, FileExactBinaryRecordBackupStore>();
        services.AddSingleton<IExactDuplicateFormatStaging, FileExactDuplicateFormatStaging>();
        services.AddSingleton<IExecutionJournalStore, JsonLinesExecutionJournalStore>();
        services.AddSingleton<IExecutionHistoryStore, FileExecutionHistoryStore>();
        services.AddSingleton<ICleanupExecutionIdGenerator, SystemCleanupExecutionIdGenerator>();
        services.AddSingleton<IRecoverySourceArtifactReader, FileRecoverySourceArtifactReader>();
        services.AddSingleton<IRecoveryPlanStore, RecoveryPlanJsonStore>();
        services.AddSingleton<IRecoveryStateBackupService, FileRecoveryStateBackupService>();
        services.AddSingleton<IRecoveryJournalStore, JsonLinesRecoveryJournalStore>();
        services.AddSingleton<FileRecoveryHistoryStore>();
        services.AddSingleton<IRecoveryHistoryStore>(provider =>
            provider.GetRequiredService<FileRecoveryHistoryStore>());
        services.AddSingleton<IRecoveryResolutionStore>(provider =>
            provider.GetRequiredService<FileRecoveryHistoryStore>());
        services.AddSingleton<IRecoveryIdGenerator, SystemRecoveryIdGenerator>();
        return services;
    }
}
