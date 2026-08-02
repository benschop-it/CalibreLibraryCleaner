namespace CalibreLibraryCleaner.Domain.Assessments;

public sealed record EpubAssessment
{
    public EpubAssessment(FormatAssessment result, EpubFeatureSummary features)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(features);
        if (!string.Equals(result.Format, "EPUB", StringComparison.Ordinal))
        {
            throw new ArgumentException("An EPUB assessment requires an EPUB result.", nameof(result));
        }
        if (features.Coverage == EpubAssessmentCoverage.FallbackReadable
            && (result.Status != AssessmentStatus.Completed || result.ScoreCap != 70))
        {
            throw new ArgumentException("A fallback-readable EPUB requires a completed assessment with a score ceiling of 70.", nameof(result));
        }
        if (features.Coverage != EpubAssessmentCoverage.FallbackReadable && result.ScoreCap is not null)
        {
            throw new ArgumentException("An EPUB score ceiling is valid only for fallback-readable coverage.", nameof(result));
        }

        Result = result;
        Features = features;
    }

    public EpubAssessment(
        Libraries.CalibreBookId calibreBookId,
        string format,
        string expectedRelativePath,
        Libraries.FormatFileFingerprint? observedFingerprint,
        AssessmentStatus status,
        QualityScore? score,
        AnalyzerVersion analyzerVersion,
        ScoringModelVersion scoringModelVersion,
        EpubFeatureSummary features,
        IEnumerable<AssessmentFinding> findings,
        int? scoreCap = null)
        : this(new FormatAssessment(
            calibreBookId,
            format,
            expectedRelativePath,
            observedFingerprint,
            status,
            score,
            analyzerVersion,
            scoringModelVersion,
            findings,
            scoreCap: scoreCap), features)
    {
    }

    public FormatAssessment Result { get; }
    public EpubFeatureSummary Features { get; }
    public Libraries.CalibreBookId CalibreBookId => Result.CalibreBookId;
    public string Format => Result.Format;
    public string ExpectedRelativePath => Result.ExpectedRelativePath;
    public Libraries.FormatFileFingerprint? ObservedFingerprint => Result.ObservedFingerprint;
    public AssessmentStatus Status => Result.Status;
    public QualityScore? Score => Result.Score;
    public QualityScore? UncappedScore => Result.UncappedScore;
    public int? ScoreCap => Result.ScoreCap;
    public AnalyzerVersion AnalyzerVersion => Result.AnalyzerVersion;
    public ScoringModelVersion ScoringModelVersion => Result.ScoringModelVersion;
    public IReadOnlyList<AssessmentFinding> Findings => Result.Findings;
    public IReadOnlyList<AssessmentScoreComponentResult> ScoreComponents => Result.ScoreComponents;
}
