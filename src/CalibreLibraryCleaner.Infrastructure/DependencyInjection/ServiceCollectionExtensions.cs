using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Infrastructure.Epub;
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
        return services;
    }
}
