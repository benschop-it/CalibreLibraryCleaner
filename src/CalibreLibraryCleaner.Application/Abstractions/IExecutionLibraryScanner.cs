using CalibreLibraryCleaner.Application.Libraries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IExecutionLibraryScanner
{
    Task<LibraryScanOutcome> ScanFreshAsync(
        string libraryRoot,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken);
}
