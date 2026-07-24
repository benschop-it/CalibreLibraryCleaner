using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Domain.Plans;

public static class CleanupPlanEligibilityPolicy
{
    public static IReadOnlyList<CleanupPlanIssue> Evaluate(
        LibrarySnapshot snapshot,
        ReviewedConsolidationRecommendation reviewed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(reviewed);
        List<CleanupPlanIssue> issues = [];
        ConsolidationRecommendation generated = reviewed.Generated;
        ExactMetadataDuplicateGroup? group = snapshot.ExactMetadataDuplicateGroups.SingleOrDefault(value => value.Id == generated.GroupId);
        ConsolidationRecommendation? current = snapshot.ConsolidationRecommendations.SingleOrDefault(value => value.GroupId == generated.GroupId);
        if (group is null || current is null || !ReferenceEquals(current, generated)
            || !generated.MemberIds.SequenceEqual(group?.Members ?? []))
            Block(issues, "PLAN.RECOMMENDATION_STALE", CleanupPlanIssueSubjectKind.Provenance, "The reviewed recommendation is not the exact current recommendation for this snapshot.");

        switch (reviewed.ReviewStatus)
        {
            case RecommendationReviewStatus.Unreviewed:
                Block(issues, "PLAN.REVIEW_REQUIRED", CleanupPlanIssueSubjectKind.Plan, "Explicit review is required before cleanup-plan generation.");
                break;
            case RecommendationReviewStatus.Deferred:
                Block(issues, "PLAN.REVIEW_DEFERRED", CleanupPlanIssueSubjectKind.Plan, "Deferred recommendations cannot produce cleanup plans.");
                break;
            case RecommendationReviewStatus.KeepSeparate:
                Block(issues, "PLAN.RECORDS_KEEP_SEPARATE", CleanupPlanIssueSubjectKind.Record, "Records marked keep-separate cannot produce a consolidation plan.");
                break;
            case RecommendationReviewStatus.NotDuplicates:
                Block(issues, "PLAN.NOT_DUPLICATES", CleanupPlanIssueSubjectKind.Group, "Records marked not-duplicates cannot produce a cleanup plan.");
                break;
            case RecommendationReviewStatus.Accepted or RecommendationReviewStatus.ManuallyAdjusted:
                break;
            default:
                Block(issues, "PLAN.REVIEW_REQUIRED", CleanupPlanIssueSubjectKind.Plan, "The review status is unsupported.");
                break;
        }

        if (reviewed.Freshness != RecommendationFreshness.Current
            || reviewed.StaleOverride is not null
            || reviewed.CurrentOverride is null
            || reviewed.CurrentOverride.ModelVersion != generated.ModelVersion
            || reviewed.CurrentOverride.InputVersion != generated.InputVersion)
            Block(issues, "PLAN.RECOMMENDATION_STALE", CleanupPlanIssueSubjectKind.Provenance, "The review, model, or canonical recommendation input is stale.");
        if (reviewed.EffectiveSelection is null)
            Block(issues, "PLAN.EFFECTIVE_SELECTION_REQUIRED", CleanupPlanIssueSubjectKind.Plan, "A complete effective reviewed selection is required.");

        HashSet<CalibreBookId> members = generated.MemberIds.ToHashSet();
        UserRecommendationOverride? userOverride = reviewed.CurrentOverride;
        if (userOverride is not null
            && (userOverride.MetadataSourceBookId is not null && !members.Contains(userOverride.MetadataSourceBookId.Value)
                || userOverride.RetainedSeparateBookIds.Any(id => !members.Contains(id))
                || userOverride.FormatOverrides.Any(value => value.SourceBookId is not null && !members.Contains(value.SourceBookId.Value))))
            Block(issues, "PLAN.OVERRIDE_OUTSIDE_GROUP", CleanupPlanIssueSubjectKind.Provenance, "The override references a record outside the duplicate group.");
        if (generated.RetainedSeparateRecords.Count > 0
            || reviewed.EffectiveSelection?.RetainedSeparateBookIds.Count > 0)
            Block(issues, "PLAN.RECORDS_KEEP_SEPARATE", CleanupPlanIssueSubjectKind.Record, "Retained-separate records make whole-group consolidation ineligible.");
        if (generated.Warnings.Any(value => value.Severity == RecommendationWarningSeverity.Blocking))
            Block(issues, "PLAN.BLOCKING_METADATA_CONFLICT", CleanupPlanIssueSubjectKind.Provenance, "A blocking recommendation conflict remains.");

        if (group is null) return Order(issues);
        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books
            .Where(value => members.Contains(value.Id)).ToDictionary(value => value.Id);
        if (books.Count != members.Count)
            Block(issues, "PLAN.RECOMMENDATION_STALE", CleanupPlanIssueSubjectKind.Record, "One or more duplicate-group records no longer exist.");

        EffectiveRecommendationSelection? effective = reviewed.EffectiveSelection;
        if (effective?.MetadataSourceBookId is null || !members.Contains(effective.MetadataSourceBookId.Value))
            Block(issues, "PLAN.TARGET_INVALID", CleanupPlanIssueSubjectKind.Record, "The reviewed metadata source cannot be the surviving target.");

        var declared = books.Values.SelectMany(book => book.Formats.Select(format => (book.Id, Format: format))).ToArray();
        var candidates = generated.FormatSelections.SelectMany(selection => selection.Candidates
            .Select(candidate => (candidate.BookId, candidate.Format, Path: Normalize(candidate.ExpectedRelativePath)))).ToArray();
        var declaredKeys = declared.Select(value => (value.Id, value.Format.Format.ToUpperInvariant(), Path: Normalize(value.Format.ExpectedRelativePath))).ToArray();
        if (declaredKeys.Distinct().Count() != declaredKeys.Length
            || candidates.Distinct().Count() != candidates.Length
            || !declaredKeys.ToHashSet().SetEquals(candidates))
            Block(issues, "PLAN.FORMAT_COVERAGE_INCOMPLETE", CleanupPlanIssueSubjectKind.Format, "The recommendation candidate graph does not exactly cover current declared group formats.");

        foreach ((CalibreBookId owner, BookFormat format) in declared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (format.FileStatus != FormatFileStatus.Present)
                Block(issues, "PLAN.SOURCE_FILE_UNAVAILABLE", CleanupPlanIssueSubjectKind.Format, "Every affected format file must be present.", owner, format.Format);
            if (format.Fingerprint is null)
                Block(issues, "PLAN.SOURCE_HASH_REQUIRED", CleanupPlanIssueSubjectKind.Format, "Every affected format file requires a current SHA-256.", owner, format.Format);
            if (format.Observation is null)
                Block(issues, "PLAN.FILE_OBSERVATION_REQUIRED", CleanupPlanIssueSubjectKind.Format, "Every affected format file requires a verified observation.", owner, format.Format);
        }

        if (effective is not null)
        {
            if (effective.FormatSelections.Select(value => value.Format).Distinct(StringComparer.Ordinal).Count() != generated.FormatSelections.Count
                || !effective.FormatSelections.Select(value => value.Format).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(generated.FormatSelections.Select(value => value.Format)))
                Block(issues, "PLAN.FORMAT_COVERAGE_INCOMPLETE", CleanupPlanIssueSubjectKind.Format, "The effective selection does not represent every canonical format.");
            foreach (FormatSourceSelection selection in effective.FormatSelections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (selection.ResolutionStatus)
                {
                    case FormatResolutionStatus.UnresolvedConflict:
                        Block(issues, "PLAN.FORMAT_UNRESOLVED", CleanupPlanIssueSubjectKind.Format, "A same-format conflict remains unresolved.", format: selection.Format);
                        continue;
                    case FormatResolutionStatus.Unavailable:
                        Block(issues, "PLAN.SOURCE_FILE_UNAVAILABLE", CleanupPlanIssueSubjectKind.Format, "A required format source is unavailable.", format: selection.Format);
                        continue;
                    case FormatResolutionStatus.ExplicitlyExcludedByUser:
                        Block(issues, "PLAN.FORMAT_EXCLUDED", CleanupPlanIssueSubjectKind.Format, "A final format cannot be excluded from a cleanup plan.", format: selection.Format);
                        Block(issues, "PLAN.UNIQUE_FORMAT_NOT_RETAINED", CleanupPlanIssueSubjectKind.Format, "Every represented canonical format requires a retained destination.", format: selection.Format);
                        continue;
                }

                RecommendationFormatCandidate? source = selection.ProposedSource;
                if (source is null || !members.Contains(source.BookId)
                    || !books.TryGetValue(source.BookId, out CalibreBook? owner)
                    || !owner.Formats.Any(format => format.Format.Equals(selection.Format, StringComparison.Ordinal)
                        && Normalize(format.ExpectedRelativePath) == Normalize(source.ExpectedRelativePath)
                        && format.FileStatus == FormatFileStatus.Present
                        && format.Fingerprint == source.Fingerprint))
                    Block(issues, "PLAN.OVERRIDE_OUTSIDE_GROUP", CleanupPlanIssueSubjectKind.Format, "A selected format source is not an exact current group association.", source?.BookId, selection.Format);
            }
        }

        if (effective?.MetadataSourceBookId is CalibreBookId target)
        {
            CalibreBookId[] removals = members.Where(value => value != target).OrderBy(value => value.Value).ToArray();
            if (removals.Contains(target)) Block(issues, "PLAN.TARGET_INVALID", CleanupPlanIssueSubjectKind.Record, "The target cannot be removed.", target);
            if (members.Count - removals.Length < 1) Block(issues, "PLAN.NO_SURVIVING_RECORD", CleanupPlanIssueSubjectKind.Record, "At least one group record must survive.");
        }

        return Order(issues);
    }

    private static void Block(
        List<CleanupPlanIssue> issues,
        string code,
        CleanupPlanIssueSubjectKind subject,
        string message,
        CalibreBookId? recordId = null,
        string? format = null) =>
        issues.Add(new(code, CleanupPlanIssueSeverity.BlockingError, subject, message, recordId, format));

    private static CleanupPlanIssue[] Order(IEnumerable<CleanupPlanIssue> issues) =>
        issues.Distinct().OrderBy(value => value.Severity).ThenBy(value => value.RecordId?.Value ?? 0)
            .ThenBy(value => value.Format, StringComparer.Ordinal).ThenBy(value => value.Code, StringComparer.Ordinal)
            .ThenBy(value => value.Explanation, StringComparer.Ordinal).ToArray();

    private static string Normalize(string path) => path.Replace('\\', '/');
}
