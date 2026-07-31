namespace CalibreLibraryCleaner.Domain.Assessments;

public sealed record PdfAssessment
{
    public PdfAssessment(FormatAssessment result, PdfFeatureSummary features)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(features);
        if (!string.Equals(result.Format, "PDF", StringComparison.Ordinal))
        {
            throw new ArgumentException("A PDF assessment requires a PDF result.", nameof(result));
        }

        if (result.Status == AssessmentStatus.Completed
            && (result.ObservedFingerprint is null || result.ObservedObservation is null))
        {
            throw new ArgumentException("A completed PDF assessment requires its verified file identity.", nameof(result));
        }

        AssessmentScoreComponentResult technical = result.ScoreComponents.Single(component => component.Id == TechnicalComponentId);
        AssessmentScoreComponentResult metadata = result.ScoreComponents.Single(component => component.Id == EmbeddedMetadataComponentId);
        ScoreBreakdown = new(technical.RawContribution, technical.Score, metadata.RawContribution, metadata.Score);
        if (result.Score is not null && result.Score.Value.Value != ScoreBreakdown.Total)
        {
            throw new ArgumentException("PDF score breakdown and total must agree.", nameof(result));
        }

        Result = result;
        Features = features;
    }

    public static AssessmentScoreComponentId TechnicalComponentId { get; } = new("technical");
    public static AssessmentScoreComponentId EmbeddedMetadataComponentId { get; } = new("embedded-metadata");
    public static IReadOnlyList<AssessmentScoreComponent> V1Components { get; } =
    [
        new(TechnicalComponentId, 85),
        new(EmbeddedMetadataComponentId, 15),
    ];

    public FormatAssessment Result { get; }
    public PdfFeatureSummary Features { get; }
    public PdfScoreBreakdown ScoreBreakdown { get; }
    public Libraries.CalibreBookId CalibreBookId => Result.CalibreBookId;
    public string Format => Result.Format;
    public string ExpectedRelativePath => Result.ExpectedRelativePath;
    public Libraries.FormatFileFingerprint? ObservedFingerprint => Result.ObservedFingerprint;
    public Libraries.FormatFileObservation? ObservedObservation => Result.ObservedObservation;
    public AssessmentStatus Status => Result.Status;
    public QualityScore? Score => Result.Score;
    public AnalyzerVersion AnalyzerVersion => Result.AnalyzerVersion;
    public ScoringModelVersion ScoringModelVersion => Result.ScoringModelVersion;
    public IReadOnlyList<AssessmentFinding> Findings => Result.Findings;
}
