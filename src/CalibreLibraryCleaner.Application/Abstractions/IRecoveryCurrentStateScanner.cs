using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Recoveries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IRecoveryCurrentStateScanner
{
    Task<RecoveryCurrentStateScanResult> ScanFreshAsync(
        string libraryRoot,
        IReadOnlyCollection<Domain.Libraries.CalibreBookId> affectedRecordIds,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken);
}
