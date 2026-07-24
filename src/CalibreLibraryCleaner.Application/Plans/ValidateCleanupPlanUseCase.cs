using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Application.Plans;

public sealed class ValidateCleanupPlanUseCase(IClock clock)
{
    public CleanupPlanOperationOutcome Execute(
        CleanupPlan plan,
        LibrarySnapshot currentSnapshot,
        ReviewedConsolidationRecommendation? currentReviewed = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.State is CleanupPlanState.Blocked or CleanupPlanState.Stale or CleanupPlanState.Revoked)
            return new(plan, plan.Validation.Issues, plan.Validation);
        DateTimeOffset now = clock.GetUtcNow().ToUniversalTime();
        List<CleanupPlanIssue> stale = CleanupPlanStalenessPolicy.Evaluate(plan, currentSnapshot, cancellationToken).ToList();
        if (currentReviewed is not null
            && currentReviewed.Generated.GroupId == plan.Definition.Provenance.GroupId
            && !CleanupPlanReviewConsistency.MatchesProvenance(plan.Definition.Provenance, currentReviewed))
            stale.Add(new("PLAN.STALE", CleanupPlanIssueSeverity.BlockingError, CleanupPlanIssueSubjectKind.Provenance,
                "The current reviewed recommendation no longer matches the cleanup plan."));
        if (stale.Count > 0)
        {
            CleanupPlanValidationResult failed = new(
                plan.Validation.Issues.Where(value => value.Severity != CleanupPlanIssueSeverity.BlockingError).Concat(stale),
                now, plan.InputIdentity);
            return new(CleanupPlanLifecyclePolicy.MarkStale(plan, failed, now), failed.Issues, failed);
        }

        CleanupPlanValidationResult current = new(
            plan.Validation.Issues.Where(value => value.Severity != CleanupPlanIssueSeverity.BlockingError),
            now,
            plan.InputIdentity);
        return new(plan, current.Issues, current);
    }
}
