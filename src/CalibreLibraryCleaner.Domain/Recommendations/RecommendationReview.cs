using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Recommendations;

public sealed record FormatRecommendationOverride(
    string Format,
    FormatOverrideAction Action,
    CalibreBookId? SourceBookId = null);

public sealed record UserRecommendationOverride
{
    public UserRecommendationOverride(
        RecommendationModelVersion modelVersion,
        RecommendationInputVersion inputVersion,
        RecommendationReviewStatus requestedStatus,
        DateTimeOffset reviewedAtUtc,
        CalibreBookId? metadataSourceBookId = null,
        IEnumerable<FormatRecommendationOverride>? formatOverrides = null,
        IEnumerable<CalibreBookId>? retainedSeparateBookIds = null)
    {
        ArgumentNullException.ThrowIfNull(modelVersion);
        ArgumentNullException.ThrowIfNull(inputVersion);
        ModelVersion = modelVersion;
        InputVersion = inputVersion;
        RequestedStatus = requestedStatus;
        ReviewedAtUtc = reviewedAtUtc.ToUniversalTime();
        MetadataSourceBookId = metadataSourceBookId;
        FormatRecommendationOverride[] formats = (formatOverrides ?? [])
            .Select(value => value with { Format = string.IsNullOrWhiteSpace(value.Format) ? string.Empty : value.Format.ToUpperInvariant() })
            .OrderBy(value => value.Format, StringComparer.Ordinal)
            .ToArray();
        if (formats.Select(value => value.Format).Distinct(StringComparer.Ordinal).Count() != formats.Length)
        {
            throw new ArgumentException("An override can contain only one action per format.", nameof(formatOverrides));
        }

        FormatOverrides = new ReadOnlyCollection<FormatRecommendationOverride>(formats);
        RetainedSeparateBookIds = new ReadOnlyCollection<CalibreBookId>((retainedSeparateBookIds ?? [])
            .Distinct()
            .OrderBy(value => value.Value)
            .ToArray());
    }

    public RecommendationModelVersion ModelVersion { get; }
    public RecommendationInputVersion InputVersion { get; }
    public RecommendationReviewStatus RequestedStatus { get; }
    public DateTimeOffset ReviewedAtUtc { get; }
    public CalibreBookId? MetadataSourceBookId { get; }
    public IReadOnlyList<FormatRecommendationOverride> FormatOverrides { get; }
    public IReadOnlyList<CalibreBookId> RetainedSeparateBookIds { get; }
}

public sealed record EffectiveRecommendationSelection(
    CalibreBookId? MetadataSourceBookId,
    IReadOnlyList<FormatSourceSelection> FormatSelections,
    IReadOnlyList<CalibreBookId> RetainedSeparateBookIds);

public sealed record ReviewedConsolidationRecommendation(
    ConsolidationRecommendation Generated,
    UserRecommendationOverride? CurrentOverride,
    EffectiveRecommendationSelection? EffectiveSelection,
    RecommendationReviewStatus ReviewStatus,
    RecommendationFreshness Freshness,
    UserRecommendationOverride? StaleOverride = null);
