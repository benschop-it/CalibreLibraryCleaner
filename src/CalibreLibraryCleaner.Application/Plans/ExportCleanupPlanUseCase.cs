using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Application.Plans;

public sealed class ExportCleanupPlanUseCase(ICleanupPlanStore store)
{
    public Task<CleanupPlanStoreWriteResult> ExecuteAsync(
        CleanupPlan plan,
        string libraryRoot,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (CleanupPlanContentDigestPolicy.Compute(plan.Definition) != plan.ContentDigest)
            return Task.FromResult(CleanupPlanStoreWriteResult.Failure("CLEANUP_PLAN_DIGEST_INVALID", "The immutable body digest is invalid."));
        return store.WriteAsync(plan, libraryRoot, destinationPath, cancellationToken);
    }
}
