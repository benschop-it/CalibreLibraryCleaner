using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Recommendations;

public sealed record ConsolidationRecommendation
{
    public ConsolidationRecommendation(
        ExactMetadataDuplicateGroupId groupId,
        IEnumerable<CalibreBookId> memberIds,
        RecommendationModelVersion modelVersion,
        RecommendationInputVersion inputVersion,
        MetadataSourceSelection? metadataSource,
        IEnumerable<FormatSourceSelection> formatSelections,
        IEnumerable<RecordRecommendation>? recordRecommendations,
        IEnumerable<RecommendationReason> reasons,
        IEnumerable<RecommendationWarning> warnings,
        RecommendationConfidence confidence)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        ArgumentNullException.ThrowIfNull(modelVersion);
        ArgumentNullException.ThrowIfNull(inputVersion);
        ArgumentNullException.ThrowIfNull(formatSelections);
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentNullException.ThrowIfNull(warnings);
        CalibreBookId[] members = memberIds.Distinct().OrderBy(id => id.Value).ToArray();
        if (members.Length < 2)
        {
            throw new ArgumentException("A recommendation requires at least two current group members.", nameof(memberIds));
        }

        FormatSourceSelection[] formats = formatSelections.OrderBy(value => value.Format, StringComparer.Ordinal).ToArray();
        if (formats.Select(value => value.Format).Distinct(StringComparer.Ordinal).Count() != formats.Length)
        {
            throw new ArgumentException("A recommendation can contain only one selection per format.", nameof(formatSelections));
        }

        RecommendationReason[] orderedReasons = OrderReasons(reasons).ToArray();
        RecommendationWarning[] orderedWarnings = OrderWarnings(warnings).ToArray();
        HashSet<string> reasonCodes = orderedReasons.Select(value => value.Code).ToHashSet(StringComparer.Ordinal);
        HashSet<string> warningCodes = orderedWarnings.Select(value => value.Code).ToHashSet(StringComparer.Ordinal);
        if (metadataSource is not null && (!members.Contains(metadataSource.SelectedBookId)
            || metadataSource.ReasonCodes.Any(code => !reasonCodes.Contains(code))))
        {
            throw new ArgumentException("The metadata selection must reference a member and linked reasons.", nameof(metadataSource));
        }

        foreach (FormatSourceSelection format in formats)
        {
            if (format.Candidates.Any(candidate => !members.Contains(candidate.BookId))
                || format.ReasonCodes.Any(code => !reasonCodes.Contains(code))
                || format.WarningCodes.Any(code => !warningCodes.Contains(code)))
            {
                throw new ArgumentException("A format selection contains an invalid member or evidence link.", nameof(formatSelections));
            }
        }

        RecordRecommendation[] records = (recordRecommendations ?? [])
            .OrderBy(value => value.BookId.Value)
            .ThenBy(value => value.Kind)
            .ToArray();
        if (records.Select(value => value.BookId).Distinct().Count() != records.Length
            || records.Any(value => !members.Contains(value.BookId)
                || value.ReasonCodes.Count == 0
                || value.ReasonCodes.Any(code => !reasonCodes.Contains(code))))
        {
            throw new ArgumentException("A record recommendation must reference a member and linked reasons.", nameof(recordRecommendations));
        }

        HashSet<CalibreBookId> retainedSeparate = records
            .Where(value => value.Kind == RecordRecommendationKind.RetainedSeparate)
            .Select(value => value.BookId)
            .ToHashSet();
        if (metadataSource is not null && retainedSeparate.Contains(metadataSource.SelectedBookId)
            || formats.Any(format => format.ProposedSource is not null
                && retainedSeparate.Contains(format.ProposedSource.BookId)))
        {
            throw new ArgumentException("A retained-separate record cannot supply consolidated metadata or formats.", nameof(recordRecommendations));
        }

        if (confidence == RecommendationConfidence.Unsupported
            && !orderedWarnings.Any(value => value.Severity == RecommendationWarningSeverity.Blocking))
        {
            throw new ArgumentException("An unsupported recommendation requires a blocking warning.", nameof(warnings));
        }

        GroupId = groupId;
        MemberIds = new ReadOnlyCollection<CalibreBookId>(members);
        ModelVersion = modelVersion;
        InputVersion = inputVersion;
        MetadataSource = metadataSource;
        FormatSelections = new ReadOnlyCollection<FormatSourceSelection>(formats);
        RecordRecommendations = new ReadOnlyCollection<RecordRecommendation>(records);
        Reasons = new ReadOnlyCollection<RecommendationReason>(orderedReasons);
        Warnings = new ReadOnlyCollection<RecommendationWarning>(orderedWarnings);
        Confidence = confidence;
    }

    public ExactMetadataDuplicateGroupId GroupId { get; }
    public IReadOnlyList<CalibreBookId> MemberIds { get; }
    public RecommendationModelVersion ModelVersion { get; }
    public RecommendationInputVersion InputVersion { get; }
    public MetadataSourceSelection? MetadataSource { get; }
    public IReadOnlyList<FormatSourceSelection> FormatSelections { get; }
    public IReadOnlyList<RecordRecommendation> RecordRecommendations { get; }
    public IReadOnlyList<RecommendationReason> Reasons { get; }
    public IReadOnlyList<RecommendationWarning> Warnings { get; }
    public RecommendationConfidence Confidence { get; }

    public IReadOnlyList<FormatSourceSelection> ProposedRetainedFormats =>
        FormatSelections.Where(value => value.ResolutionStatus == FormatResolutionStatus.Selected).ToArray();

    public IReadOnlyList<FormatSourceSelection> UnresolvedConflicts =>
        FormatSelections.Where(value => value.ResolutionStatus is FormatResolutionStatus.UnresolvedConflict or FormatResolutionStatus.Unavailable).ToArray();

    public IReadOnlyList<RecordRecommendation> ProposedRedundantRecords =>
        RecordRecommendations.Where(value => value.Kind == RecordRecommendationKind.ProposedRedundant).ToArray();

    public IReadOnlyList<RecordRecommendation> RetainedSeparateRecords =>
        RecordRecommendations.Where(value => value.Kind == RecordRecommendationKind.RetainedSeparate).ToArray();

    private static IOrderedEnumerable<RecommendationReason> OrderReasons(IEnumerable<RecommendationReason> values) => values
        .OrderBy(value => value.SubjectKind)
        .ThenBy(value => value.Format, StringComparer.Ordinal)
        .ThenBy(value => value.BookId?.Value ?? 0)
        .ThenBy(value => value.Code, StringComparer.Ordinal)
        .ThenBy(value => value.Explanation, StringComparer.Ordinal);

    private static IOrderedEnumerable<RecommendationWarning> OrderWarnings(IEnumerable<RecommendationWarning> values) => values
        .OrderBy(value => value.SubjectKind)
        .ThenBy(value => value.Format, StringComparer.Ordinal)
        .ThenBy(value => value.BookId?.Value ?? 0)
        .ThenBy(value => value.Code, StringComparer.Ordinal)
        .ThenBy(value => value.Explanation, StringComparer.Ordinal);
}
