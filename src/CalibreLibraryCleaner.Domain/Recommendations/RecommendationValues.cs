namespace CalibreLibraryCleaner.Domain.Recommendations;

public sealed record RecommendationModelVersion
{
    public RecommendationModelVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public static RecommendationModelVersion V1 { get; } = new("consolidation-recommendation/1.0.2");

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record RecommendationInputVersion
{
    public RecommendationInputVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public enum RecommendationConfidence
{
    Deterministic,
    High,
    Medium,
    Low,
    ManualReviewRequired,
    Unsupported,
}

public enum RecommendationDecisionStrength
{
    Safe,
    Strong,
    Ambiguous,
    Unsupported,
}

public enum RecommendationReviewStatus
{
    Unreviewed,
    Accepted,
    ManuallyAdjusted,
    Deferred,
    KeepSeparate,
    NotDuplicates,
}

public enum RecommendationFreshness
{
    Current,
    Stale,
}

public enum FormatResolutionStatus
{
    Selected,
    UnresolvedConflict,
    Unavailable,
    ExplicitlyExcludedByUser,
}

public enum RecommendationWarningSeverity
{
    Advisory,
    ManualReview,
    Blocking,
}

public enum RecommendationSubjectKind
{
    Group,
    Metadata,
    Format,
    Record,
    Assessment,
    User,
}

public enum RecordRecommendationKind
{
    ProposedRedundant,
    RetainedSeparate,
}

public enum FormatOverrideAction
{
    SelectSource,
    MarkUnresolved,
    ExcludeFinalFormat,
}
