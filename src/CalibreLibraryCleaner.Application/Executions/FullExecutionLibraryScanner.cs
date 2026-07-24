using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed class FullExecutionLibraryScanner(ScanLibraryUseCase scanLibrary) : IExecutionLibraryScanner
{
    public Task<LibraryScanOutcome> ScanFreshAsync(
        string libraryRoot,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken) =>
        scanLibrary.ExecuteAsync(libraryRoot, progress, cancellationToken);
}
