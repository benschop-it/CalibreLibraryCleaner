using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Domain.Plans;

public sealed record CleanupPlanFormatSelectionProvenance(
    string Format,
    FormatResolutionStatus ResolutionStatus,
    CalibreBookId? SelectedRecordId,
    IReadOnlyList<CalibreBookId> CandidateRecordIds,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> WarningCodes);

public sealed record CleanupPlanOverrideProvenance(
    RecommendationReviewStatus RequestedStatus,
    DateTimeOffset ReviewedAtUtc,
    CalibreBookId? MetadataSourceRecordId,
    IReadOnlyList<string> FormatActions,
    IReadOnlyList<CalibreBookId> RetainedSeparateRecordIds);

public sealed record CleanupPlanProvenance
{
    public CleanupPlanProvenance(
        ExactMetadataDuplicateGroupId groupId,
        string normalizedTitle,
        IEnumerable<string> normalizedAuthors,
        string groupReasonCode,
        RecommendationModelVersion recommendationModelVersion,
        RecommendationInputVersion recommendationInputVersion,
        RecommendationConfidence generatedConfidence,
        CalibreBookId generatedMetadataSourceRecordId,
        IEnumerable<CleanupPlanFormatSelectionProvenance> generatedFormatSelections,
        IEnumerable<string> generatedReasonCodes,
        IEnumerable<string> generatedWarningCodes,
        RecommendationReviewStatus reviewStatus,
        RecommendationFreshness freshness,
        CalibreBookId reviewedMetadataSourceRecordId,
        IEnumerable<CleanupPlanFormatSelectionProvenance> reviewedFormatSelections,
        CleanupPlanOverrideProvenance userOverride,
        CleanupPlanSchemaVersion schemaVersion,
        CleanupPlanPolicyVersion policyVersion,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupReasonCode);
        if (!Enum.IsDefined(generatedConfidence)) throw new ArgumentOutOfRangeException(nameof(generatedConfidence));
        if (!Enum.IsDefined(reviewStatus)) throw new ArgumentOutOfRangeException(nameof(reviewStatus));
        if (!Enum.IsDefined(freshness)) throw new ArgumentOutOfRangeException(nameof(freshness));
        if (!Enum.IsDefined(userOverride.RequestedStatus)) throw new ArgumentOutOfRangeException(nameof(userOverride));
        if (freshness != RecommendationFreshness.Current
            || reviewStatus is not (RecommendationReviewStatus.Accepted or RecommendationReviewStatus.ManuallyAdjusted))
            throw new ArgumentException("Cleanup-plan provenance requires a current accepted or adjusted review.");
        GroupId = groupId;
        NormalizedTitle = normalizedTitle;
        NormalizedAuthors = Array.AsReadOnly(normalizedAuthors.Order(StringComparer.Ordinal).ToArray());
        GroupReasonCode = groupReasonCode;
        RecommendationModelVersion = recommendationModelVersion;
        RecommendationInputVersion = recommendationInputVersion;
        GeneratedConfidence = generatedConfidence;
        GeneratedMetadataSourceRecordId = generatedMetadataSourceRecordId;
        GeneratedFormatSelections = CopySelections(generatedFormatSelections);
        GeneratedReasonCodes = Array.AsReadOnly(generatedReasonCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        GeneratedWarningCodes = Array.AsReadOnly(generatedWarningCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        ReviewStatus = reviewStatus;
        Freshness = freshness;
        ReviewedMetadataSourceRecordId = reviewedMetadataSourceRecordId;
        ReviewedFormatSelections = CopySelections(reviewedFormatSelections);
        UserOverride = new(
            userOverride.RequestedStatus,
            userOverride.ReviewedAtUtc.ToUniversalTime(),
            userOverride.MetadataSourceRecordId,
            Array.AsReadOnly(userOverride.FormatActions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(userOverride.RetainedSeparateRecordIds.Distinct().OrderBy(value => value.Value).ToArray()));
        SchemaVersion = schemaVersion;
        PolicyVersion = policyVersion;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
    }

    public ExactMetadataDuplicateGroupId GroupId { get; }
    public string NormalizedTitle { get; }
    public IReadOnlyList<string> NormalizedAuthors { get; }
    public string GroupReasonCode { get; }
    public RecommendationModelVersion RecommendationModelVersion { get; }
    public RecommendationInputVersion RecommendationInputVersion { get; }
    public RecommendationConfidence GeneratedConfidence { get; }
    public CalibreBookId GeneratedMetadataSourceRecordId { get; }
    public IReadOnlyList<CleanupPlanFormatSelectionProvenance> GeneratedFormatSelections { get; }
    public IReadOnlyList<string> GeneratedReasonCodes { get; }
    public IReadOnlyList<string> GeneratedWarningCodes { get; }
    public RecommendationReviewStatus ReviewStatus { get; }
    public RecommendationFreshness Freshness { get; }
    public CalibreBookId ReviewedMetadataSourceRecordId { get; }
    public IReadOnlyList<CleanupPlanFormatSelectionProvenance> ReviewedFormatSelections { get; }
    public CleanupPlanOverrideProvenance UserOverride { get; }
    public CleanupPlanSchemaVersion SchemaVersion { get; }
    public CleanupPlanPolicyVersion PolicyVersion { get; }
    public DateTimeOffset CreatedAtUtc { get; }

    private static System.Collections.ObjectModel.ReadOnlyCollection<CleanupPlanFormatSelectionProvenance> CopySelections(
        IEnumerable<CleanupPlanFormatSelectionProvenance> source) =>
        Array.AsReadOnly(source.OrderBy(value => value.Format, StringComparer.Ordinal)
            .Select(value =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value.Format);
                if (!Enum.IsDefined(value.ResolutionStatus)) throw new ArgumentOutOfRangeException(nameof(source));
                return value with
                {
                    Format = value.Format.ToUpperInvariant(),
                    CandidateRecordIds = Array.AsReadOnly(value.CandidateRecordIds.Distinct().OrderBy(id => id.Value).ToArray()),
                    ReasonCodes = Array.AsReadOnly(value.ReasonCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
                    WarningCodes = Array.AsReadOnly(value.WarningCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
                };
            }).ToArray());
}
