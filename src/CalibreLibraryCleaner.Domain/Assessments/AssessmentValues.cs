namespace CalibreLibraryCleaner.Domain.Assessments;

public readonly record struct QualityScore
{
    public QualityScore(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
        Value = value;
    }

    public int Value { get; }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record AnalyzerVersion
{
    public AnalyzerVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record ScoringModelVersion
{
    public ScoringModelVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record AssessmentScoreComponentId
{
    public static AssessmentScoreComponentId Overall { get; } = new("overall");

    public AssessmentScoreComponentId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 64 || normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("A score component identifier must be a bounded lowercase token.", nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record AssessmentScoreComponent
{
    public AssessmentScoreComponent(AssessmentScoreComponentId id, int maximumScore)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumScore);
        Id = id;
        MaximumScore = maximumScore;
    }

    public AssessmentScoreComponentId Id { get; }

    public int MaximumScore { get; }
}

public sealed record AssessmentScoreComponentResult(
    AssessmentScoreComponentId Id,
    int MaximumScore,
    int RawContribution,
    int Score);
