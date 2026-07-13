using CalibreLibraryCleaner.Application.Libraries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface ICalibreMetadataReader
{
    Task<CalibreCatalogReadOutcome> ReadAsync(
        ValidatedLibraryLocation library,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken);
}
