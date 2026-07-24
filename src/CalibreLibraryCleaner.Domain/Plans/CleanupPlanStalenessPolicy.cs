using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Domain.Plans;

public static class CleanupPlanStalenessPolicy
{
    public static IReadOnlyList<CleanupPlanIssue> Evaluate(
        CleanupPlan plan,
        LibrarySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshot);
        List<CleanupPlanIssue> issues = [];
        ExpectedLibraryState expected = plan.Definition.ExpectedLibraryState;
        if (plan.SchemaVersion != CleanupPlanSchemaVersion.V1 || plan.PolicyVersion != CleanupPlanPolicyVersion.V1)
            Stale(issues, "The cleanup-plan schema or policy is no longer supported.");
        if (!string.Equals(expected.LibraryUuid, snapshot.Identity.CalibreLibraryUuid, StringComparison.Ordinal)
            || expected.SchemaVersion != snapshot.Identity.SchemaVersion)
            Stale(issues, "The source-library identity or schema changed.");

        ExactMetadataDuplicateGroup? group = snapshot.ExactMetadataDuplicateGroups.SingleOrDefault(value => value.Id == expected.GroupId);
        if (group is null
            || !group.Members.SequenceEqual(expected.MemberIds)
            || !string.Equals(group.Identity.Title.Value, plan.Definition.Provenance.NormalizedTitle, StringComparison.Ordinal)
            || !group.Identity.Authors.Names.Select(value => value.Value).SequenceEqual(plan.Definition.Provenance.NormalizedAuthors)
            || !string.Equals(group.MatchReason.Code, plan.Definition.Provenance.GroupReasonCode, StringComparison.Ordinal))
            Stale(issues, "The duplicate group identity, evidence, or membership changed.");

        ConsolidationRecommendation? recommendation = snapshot.ConsolidationRecommendations.SingleOrDefault(value => value.GroupId == expected.GroupId);
        if (recommendation is null
            || recommendation.ModelVersion != expected.RecommendationModelVersion
            || recommendation.InputVersion != expected.RecommendationInputVersion
            || recommendation.ModelVersion != plan.InputIdentity.RecommendationModelVersion
            || recommendation.InputVersion != plan.InputIdentity.RecommendationInputVersion)
            Stale(issues, "The generated recommendation model or canonical relevant input changed.");

        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books.ToDictionary(value => value.Id);
        foreach (ExpectedRecordState record in expected.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!books.TryGetValue(record.RecordId, out CalibreBook? current) || !RecordMatches(record, current))
                Stale(issues, "Expected record metadata, managed path, or format state changed.", record.RecordId);
        }

        if (books.Values.Where(value => expected.MemberIds.Contains(value.Id)).Select(value => value.Id).ToHashSet()
            .Count != expected.MemberIds.Count)
            Stale(issues, "One or more expected records are missing.");
        return issues.Distinct().OrderBy(value => value.RecordId?.Value ?? 0)
            .ThenBy(value => value.Code, StringComparer.Ordinal).ToArray();
    }

    private static bool RecordMatches(ExpectedRecordState expected, CalibreBook current)
    {
        BookPublicationMetadata publication = current.PublicationMetadata;
        if (!string.Equals(expected.Title, current.Title, StringComparison.Ordinal)
            || !string.Equals(expected.AuthorSort, current.AuthorSort, StringComparison.Ordinal)
            || !expected.Authors.SequenceEqual(current.Authors.Select(value => new ExpectedAuthorState(value.Id, value.Name, value.SortName)))
            || !expected.Identifiers.SequenceEqual(current.Identifiers.OrderBy(value => value.Type, StringComparer.Ordinal)
                .ThenBy(value => value.Value, StringComparer.Ordinal).Select(value => new ExpectedIdentifierState(value.Type, value.Value)))
            || !string.Equals(expected.Publisher, publication.Publisher, StringComparison.Ordinal)
            || expected.PublicationDate != publication.PublicationDate?.ToUniversalTime()
            || !string.Equals(expected.Series, publication.Series, StringComparison.Ordinal)
            || expected.SeriesIndex != publication.SeriesIndex
            || !expected.Languages.SequenceEqual(publication.Languages)
            || expected.HasCover != publication.HasCover
            || !string.Equals(expected.RelativeDirectory, Normalize(current.RelativeDirectory), StringComparison.Ordinal))
            return false;
        ExpectedFormatState[] currentFormats;
        try
        {
            currentFormats = current.Formats.Select(value => new ExpectedFormatState(
                current.Id,
                value.Format,
                value.StoredFileName,
                value.ExpectedRelativePath,
                value.FileStatus,
                value.Fingerprint!,
                value.Observation!)).OrderBy(value => value.Format, StringComparer.Ordinal)
                .ThenBy(value => value.RelativePath, StringComparer.Ordinal).ToArray();
        }
        catch (ArgumentException)
        {
            return false;
        }

        return expected.Formats.Count == currentFormats.Length
            && expected.Formats.Zip(currentFormats).All(pair => FormatMatches(pair.First, pair.Second));
    }

    private static bool FormatMatches(ExpectedFormatState left, ExpectedFormatState right) =>
        left.RecordId == right.RecordId
        && left.Format == right.Format
        && left.StoredFileName == right.StoredFileName
        && left.RelativePath == right.RelativePath
        && left.Status == right.Status
        && left.Fingerprint == right.Fingerprint
        && left.Observation == right.Observation
        && left.ObservationSourceVersion == right.ObservationSourceVersion;

    private static void Stale(List<CleanupPlanIssue> issues, string message, CalibreBookId? recordId = null) =>
        issues.Add(new("PLAN.STALE", CleanupPlanIssueSeverity.BlockingError, CleanupPlanIssueSubjectKind.Plan, message, recordId));

    private static string Normalize(string path) => path.Replace('\\', '/');
}
