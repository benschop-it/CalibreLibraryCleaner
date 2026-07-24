namespace CalibreLibraryCleaner.Domain.Plans;

public static class CleanupPlanRequiredIssuePolicy
{
    public static IReadOnlyList<CleanupPlanIssue> Create(CleanupPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<CleanupPlanIssue> issues =
        [
            new("PLAN.TARGET_IS_METADATA_SOURCE", CleanupPlanIssueSeverity.Information, CleanupPlanIssueSubjectKind.Record,
                "Cleanup-plan policy V1 uses the reviewed metadata source as the surviving target.", definition.TargetRecordId),
            new("PLAN.NON_EXECUTABLE", CleanupPlanIssueSeverity.Information, CleanupPlanIssueSubjectKind.Plan,
                "This plan is descriptive and cannot modify Calibre."),
            new("PLAN.BACKUPS_NOT_CREATED", CleanupPlanIssueSeverity.Information, CleanupPlanIssueSubjectKind.Backup,
                "Backup entries are requirements only; no backup has been created."),
            new("PLAN.EXECUTION_REVALIDATION_REQUIRED", CleanupPlanIssueSeverity.Information, CleanupPlanIssueSubjectKind.Plan,
                "A later execution milestone must revalidate all expected state immediately before any change."),
        ];
        foreach (FormatRemovalInstruction removal in definition.FormatRemovals
                     .Where(value => value.Reason == FormatRemovalReason.ReviewedNonIdenticalReplacement))
        {
            issues.Add(new("PLAN.REVIEWED_NONIDENTICAL_REMOVAL", CleanupPlanIssueSeverity.Warning,
                CleanupPlanIssueSubjectKind.Format,
                "The current review selected another byte-distinct same-format source; content equivalence is not asserted and this file requires backup.",
                removal.RecordId, removal.Format));
        }

        foreach (ExpectedRecordState record in definition.ExpectedLibraryState.Records.Where(value => value.HasCover))
        {
            issues.Add(new("PLAN.COVER_FILE_STATE_DEFERRED", CleanupPlanIssueSeverity.Information,
                CleanupPlanIssueSubjectKind.Backup,
                "The catalog reports a cover; its physical state must be resolved, backed up, and verified before later execution.",
                record.RecordId));
        }

        return issues.OrderBy(value => value.Severity).ThenBy(value => value.RecordId?.Value ?? 0)
            .ThenBy(value => value.Format, StringComparer.Ordinal).ThenBy(value => value.Code, StringComparer.Ordinal).ToArray();
    }

    public static bool ContainsAllRequired(CleanupPlanDefinition definition, IEnumerable<CleanupPlanIssue> actual)
    {
        CleanupPlanIssue[] values = actual.ToArray();
        return Create(definition).All(required => values.Any(value => Equivalent(value, required)));
    }

    private static bool Equivalent(CleanupPlanIssue left, CleanupPlanIssue right) =>
        left.Code == right.Code && left.Severity == right.Severity && left.SubjectKind == right.SubjectKind
        && left.Explanation == right.Explanation && left.RecordId == right.RecordId && left.Format == right.Format
        && left.Evidence.OrderBy(value => value.Key, StringComparer.Ordinal)
            .SequenceEqual(right.Evidence.OrderBy(value => value.Key, StringComparer.Ordinal));
}
