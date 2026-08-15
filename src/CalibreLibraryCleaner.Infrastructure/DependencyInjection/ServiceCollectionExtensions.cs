using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Infrastructure.Bibliographic;
using CalibreLibraryCleaner.Infrastructure.Caches;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.Epub;
using CalibreLibraryCleaner.Infrastructure.Execution;
using CalibreLibraryCleaner.Infrastructure.Hashing;
using CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;
using CalibreLibraryCleaner.Infrastructure.LocalModels;
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
        services.AddSingleton(new FormatHashCacheOptions());
        services.AddSingleton<IFormatHashCacheKeyFactory, Sha256FormatHashCacheKeyFactory>();
        services.AddSingleton<IFormatHashCache, FileFormatHashCache>();
        services.AddSingleton<IFormatFileHasher, StreamingSha256FormatFileHasher>();
        services.AddSingleton<IFormatFileProbe, PhysicalFormatFileProbe>();
        services.AddSingleton(new LibrarySnapshotStorageOptions());
        services.AddSingleton<ILibrarySnapshotStore, VersionedJsonLibrarySnapshotStore>();
        services.AddSingleton<ILibraryStateStore, VersionedJsonLibraryStateStore>();
        services.AddSingleton<VersOneEpubInspector>();
        services.AddSingleton<IEpubInspector>(provider => provider.GetRequiredService<VersOneEpubInspector>());
        services.AddSingleton<IEpubContentSignatureInspector>(provider => provider.GetRequiredService<VersOneEpubInspector>());
        services.AddSingleton(new EpubContentSignatureCacheOptions());
        services.AddSingleton<IEpubContentSignatureCache, FileEpubContentSignatureCache>();
        bool openLibraryEnabled = !string.Equals(
            Environment.GetEnvironmentVariable("CALIBRE_OPEN_LIBRARY_ENABLED"), "0", StringComparison.Ordinal);
        OpenLibraryOptions openLibraryOptions = new(
            enabled: openLibraryEnabled,
            contact: Environment.GetEnvironmentVariable("CALIBRE_OPEN_LIBRARY_CONTACT"));
        services.AddSingleton(openLibraryOptions);
        services.AddSingleton(new BibliographicEnrichmentOptions(enabled: openLibraryEnabled));
        services.AddSingleton(new BibliographicResolutionCacheOptions());
        services.AddSingleton<IBibliographicResolutionCache, FileBibliographicResolutionCache>();
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                | System.Net.DecompressionMethods.Deflate,
        })
        {
            BaseAddress = new("https://openlibrary.org/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(65),
        });
        services.AddSingleton<OpenLibraryBibliographicProvider>();
        services.AddSingleton<IBibliographicProvider>(provider =>
            provider.GetRequiredService<OpenLibraryBibliographicProvider>());
        string? embeddingModel = Environment.GetEnvironmentVariable("CALIBRE_OLLAMA_EMBEDDING_MODEL");
        string? embeddingVersion = Environment.GetEnvironmentVariable("CALIBRE_OLLAMA_EMBEDDING_MODEL_VERSION");
        string? ollamaRuntimeVersion = Environment.GetEnvironmentVariable("CALIBRE_OLLAMA_RUNTIME_VERSION");
        OllamaEmbeddingOptions embeddingOptions = new(
            embeddingModel, embeddingVersion, ollamaRuntimeVersion);
        services.AddSingleton(embeddingOptions);
        services.AddSingleton(new LocalEmbeddingComparisonCacheOptions());
        services.AddSingleton<ILocalEmbeddingComparisonCache, FileLocalEmbeddingComparisonCache>();
        services.AddSingleton<ILocalEmbeddingProvider>(_ =>
        {
            HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false })
            {
                BaseAddress = new("http://127.0.0.1:11434/", UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(125),
            };
            return new OllamaLocalEmbeddingProvider(client, embeddingOptions);
        });
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
