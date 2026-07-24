using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Plans;

public sealed class ImportCleanupPlanUseCase(
    ICleanupPlanStore store,
    ValidateCleanupPlanUseCase validator)
{
    public async Task<CleanupPlanOperationOutcome> ExecuteAsync(
        LibrarySnapshot currentSnapshot,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        CleanupPlanStoreReadResult read = await store.ReadAsync(
            currentSnapshot.Identity.LibraryRoot,
            sourcePath,
            cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
            return new(null, [new(read.Error!.Code, Domain.Plans.CleanupPlanIssueSeverity.BlockingError,
                Domain.Plans.CleanupPlanIssueSubjectKind.Plan, read.Error.Message)]);
        return validator.Execute(read.Plan!, currentSnapshot, cancellationToken: cancellationToken);
    }
}
