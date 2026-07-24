using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.Epub;
using CalibreLibraryCleaner.Infrastructure.Execution;
using CalibreLibraryCleaner.Infrastructure.Hashing;
using CalibreLibraryCleaner.Infrastructure.Paths;
using CalibreLibraryCleaner.Infrastructure.Plans;
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
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ILibraryPathResolver, LibraryPathResolver>();
        services.AddSingleton<ICalibreMetadataReader, SqliteCalibreMetadataReader>();
        services.AddSingleton<IFormatFileHasher, StreamingSha256FormatFileHasher>();
        services.AddSingleton<IEpubInspector, VersOneEpubInspector>();
        services.AddSingleton<IRecommendationExporter, VersionedJsonRecommendationExporter>();
        services.AddSingleton<ICleanupPlanIdGenerator, SystemCleanupPlanIdGenerator>();
        services.AddSingleton<ICleanupPlanStore, VersionedJsonCleanupPlanStore>();
        services.AddSingleton(new CalibreExecutionOptions());
        services.AddSingleton(new ExecutionStorageOptions());
        services.AddSingleton<DirectCalibreProcessRunner>();
        services.AddSingleton<ICalibreToolDiscovery, CalibreToolDiscovery>();
        services.AddSingleton<ICalibreCommandGateway, CalibreCommandGateway>();
        services.AddSingleton<ICleanupExecutionLease, FileCleanupExecutionLease>();
        services.AddSingleton<IExecutionBackupStore, FileExecutionBackupStore>();
        services.AddSingleton<IExecutionJournalStore, JsonLinesExecutionJournalStore>();
        services.AddSingleton<IExecutionHistoryStore, FileExecutionHistoryStore>();
        services.AddSingleton<ICleanupExecutionIdGenerator, SystemCleanupExecutionIdGenerator>();
        return services;
    }
}
