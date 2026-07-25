using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Abstractions;

public interface IRecoveryPlanStore
{
    Task<RecoveryPlanStoreResult> WriteCreateNewAsync(
        RecoveryPlan plan,
        string destinationPath,
        string libraryRoot,
        CancellationToken cancellationToken);

    Task<RecoveryPlanStoreResult> ReadAsync(
        string sourcePath,
        string libraryRoot,
        CancellationToken cancellationToken);
}
