using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;

internal static class TestServices
{
    public static ServiceProvider CreateProvider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        services.AddSingleton(new LibraryAnalysisOptions());
        return services.BuildServiceProvider();
    }

    public static ScanLibraryUseCase CreateScanUseCase(ServiceProvider provider) => new(
        provider.GetRequiredService<ILibraryPathResolver>(),
        provider.GetRequiredService<ICalibreMetadataReader>(),
        provider.GetRequiredService<IFormatFileHasher>(),
        provider.GetRequiredService<IClock>(),
        provider.GetRequiredService<LibraryAnalysisOptions>());
}
