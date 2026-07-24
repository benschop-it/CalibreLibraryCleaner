using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Application.Executions;

internal static class ExecutionPreflightPolicy
{
    private const long BackupMarginBytes = 64L * 1024 * 1024;

    public static IReadOnlyList<ExecutionIssue> Evaluate(CleanupPlan plan, LibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshot);
        List<ExecutionIssue> issues = [];
        if (plan.State != CleanupPlanState.Approved || plan.Approval is null)
            Block(issues, "EXECUTION.APPROVAL_REQUIRED", "Only a currently approved cleanup plan can be executed.");
        else if (plan.Approval.ContentDigest != plan.ContentDigest)
            Block(issues, "EXECUTION.APPROVAL_DIGEST_MISMATCH", "The approval is not bound to the current canonical plan body.");
        if (!plan.Validation.IsValid)
            Block(issues, "EXECUTION.PLAN_BLOCKED", "The cleanup plan contains blocking validation issues.");
        if (CleanupPlanContentDigestPolicy.Compute(plan.Definition) != plan.ContentDigest
            || plan.InputIdentity.DefinitionDigest != plan.ContentDigest)
            Block(issues, "EXECUTION.PLAN_TAMPERED", "The cleanup-plan canonical body hash is invalid.");

        foreach (CleanupPlanIssue stale in CleanupPlanStalenessPolicy.Evaluate(plan, snapshot))
            Block(issues, "EXECUTION.PLAN_STALE", stale.Explanation, stale.RecordId, stale.Format);
        foreach (CleanupPlanIssue unsafeIssue in CleanupPlanSafetyPolicy.Validate(plan.Definition)
                     .Where(value => value.Severity == CleanupPlanIssueSeverity.BlockingError))
            Block(issues, "EXECUTION.PLAN_UNSAFE", unsafeIssue.Explanation, unsafeIssue.RecordId, unsafeIssue.Format);

        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books.ToDictionary(value => value.Id);
        foreach (ExpectedRecordState expectedRecord in plan.Definition.ExpectedLibraryState.Records)
        {
            if (!books.TryGetValue(expectedRecord.RecordId, out CalibreBook? current))
            {
                Block(issues, "EXECUTION.RECORD_MISSING", "An affected record no longer exists.", expectedRecord.RecordId);
                continue;
            }

            foreach (ExpectedFormatState expectedFormat in expectedRecord.Formats)
            {
                BookFormat? format = current.Formats.SingleOrDefault(value => value.Format == expectedFormat.Format);
                if (format is null || format.FileStatus != FormatFileStatus.Present || format.Fingerprint != expectedFormat.Fingerprint)
                    Block(issues, "EXECUTION.SOURCE_FORMAT_INVALID", "A required source format is missing, unreadable, changed, or ambiguous.", expectedRecord.RecordId, expectedFormat.Format);
            }
        }

        return issues.Distinct().OrderBy(value => value.RecordId?.Value ?? 0)
            .ThenBy(value => value.Format, StringComparer.Ordinal).ThenBy(value => value.Code, StringComparer.Ordinal).ToArray();
    }

    public static long EstimateRequiredBackupBytes(CleanupPlan plan)
    {
        try
        {
            long formats = plan.Definition.ExpectedLibraryState.Records.SelectMany(value => value.Formats)
                .Aggregate(0L, (total, format) => checked(total + format.Fingerprint.SizeInBytes));
            return checked(formats * 3 + BackupMarginBytes);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static void Block(List<ExecutionIssue> issues, string code, string explanation, CalibreBookId? recordId = null, string? format = null) =>
        issues.Add(new(code, ExecutionIssueSeverity.BlockingError, explanation, recordId, format));
}
