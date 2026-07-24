using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Application.Plans;

public sealed class ApproveCleanupPlanUseCase(IClock clock)
{
    public CleanupPlanOperationOutcome Execute(
        CleanupPlan plan,
        LibrarySnapshot currentSnapshot,
        ReviewedConsolidationRecommendation? currentReviewed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        try
        {
            DateTimeOffset now = clock.GetUtcNow().ToUniversalTime();
            IReadOnlyList<CleanupPlanIssue> stale = CleanupPlanStalenessPolicy.Evaluate(plan, currentSnapshot);
            if (stale.Count > 0 || currentReviewed is null
                || currentReviewed.Generated.GroupId != plan.Definition.Provenance.GroupId
                || !CleanupPlanReviewConsistency.MatchesProvenance(plan.Definition.Provenance, currentReviewed))
                throw new InvalidOperationException("The plan is not current against a locally reviewed recommendation.");
            CleanupPlanValidationResult validation = new(
                plan.Validation.Issues.Where(value => value.Severity != CleanupPlanIssueSeverity.BlockingError),
                now, plan.InputIdentity);
            CleanupPlan approved = CleanupPlanLifecyclePolicy.Approve(plan, validation, now);
            return new(approved, approved.Validation.Issues, approved.Validation);
        }
        catch (InvalidOperationException)
        {
            CleanupPlanIssue issue = new("PLAN.APPROVAL_NOT_ALLOWED", CleanupPlanIssueSeverity.BlockingError,
                CleanupPlanIssueSubjectKind.Approval, "Blocked, stale, revoked, unvalidated, or non-valid plans cannot be approved.");
            return new(null, [issue]);
        }
    }
}
