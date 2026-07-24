using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Application.Plans;

public sealed class RevokeCleanupPlanUseCase(IClock clock)
{
    public CleanupPlanOperationOutcome Execute(CleanupPlan plan, string? reason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(reason))
            return new(null, [new("PLAN.REVOCATION_REASON_REQUIRED", CleanupPlanIssueSeverity.BlockingError,
                CleanupPlanIssueSubjectKind.Approval, "A bounded revocation reason is required.")]);
        try
        {
            CleanupPlan revoked = CleanupPlanLifecyclePolicy.Revoke(plan, reason, clock.GetUtcNow());
            return new(revoked, revoked.Validation.Issues);
        }
        catch (ArgumentException)
        {
            return new(null, [new("PLAN.REVOCATION_REASON_INVALID", CleanupPlanIssueSeverity.BlockingError,
                CleanupPlanIssueSubjectKind.Approval, "The revocation reason is invalid or too long.")]);
        }
        catch (InvalidOperationException)
        {
            return new(null, [new("PLAN.REVOCATION_NOT_ALLOWED", CleanupPlanIssueSeverity.BlockingError,
                CleanupPlanIssueSubjectKind.Approval, "Only an approved cleanup plan can be revoked.")]);
        }
    }
}
