using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Application.Plans;

public sealed record CleanupPlanGenerationOutcome(
    CleanupPlan? Plan,
    CleanupPlanValidationResult Validation)
{
    public bool IsSuccess => Plan is not null;
}

public sealed record CleanupPlanOperationOutcome(
    CleanupPlan? Plan,
    IReadOnlyList<CleanupPlanIssue> Issues,
    CleanupPlanValidationResult? Validation = null)
{
    public bool IsSuccess => Plan is not null && Issues.All(value => value.Severity != CleanupPlanIssueSeverity.BlockingError);
}
