using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Assessments;

public sealed record FormatAssessment
{
    public FormatAssessment(
        CalibreBookId calibreBookId,
        string format,
        string expectedRelativePath,
        FormatFileFingerprint? observedFingerprint,
        AssessmentStatus status,
        QualityScore? score,
        AnalyzerVersion analyzerVersion,
        ScoringModelVersion scoringModelVersion,
        EpubFeatureSummary features,
        IEnumerable<AssessmentFinding> findings)
    {
        if (!string.Equals(format, "EPUB", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only EPUB assessments are supported.", nameof(format));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRelativePath);
        string normalizedPath = expectedRelativePath.Replace('\\', '/');
        if (normalizedPath.StartsWith('/')
            || normalizedPath.Contains(':', StringComparison.Ordinal)
            || normalizedPath.Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new ArgumentException("The assessment path must be presentation-safe and relative.", nameof(expectedRelativePath));
        }

        ArgumentNullException.ThrowIfNull(analyzerVersion);
        ArgumentNullException.ThrowIfNull(scoringModelVersion);
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(findings);
        AssessmentFinding[] ordered = findings
            .OrderBy(finding => SeverityOrder(finding.Severity))
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.EvidenceKey, StringComparer.Ordinal)
            .ThenBy(finding => finding.Explanation, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("An assessment requires findings.", nameof(findings));
        }

        bool hasDisqualifier = ordered.Any(finding => finding.Severity == FindingSeverity.Disqualifying);
        if (status == AssessmentStatus.Disqualified)
        {
            if (!hasDisqualifier || score is not null)
            {
                throw new ArgumentException("A disqualified assessment requires a disqualifier and no score.", nameof(status));
            }
        }
        else
        {
            int expectedScore = Math.Clamp(ordered.Sum(finding => finding.ScoreAdjustment), 0, 100);
            if (hasDisqualifier || score is null || score.Value.Value != expectedScore)
            {
                throw new ArgumentException("A completed score must be derived entirely from its findings.", nameof(score));
            }
        }

        CalibreBookId = calibreBookId;
        Format = "EPUB";
        ExpectedRelativePath = normalizedPath;
        ObservedFingerprint = observedFingerprint;
        Status = status;
        Score = score;
        AnalyzerVersion = analyzerVersion;
        ScoringModelVersion = scoringModelVersion;
        Features = features;
        Findings = new ReadOnlyCollection<AssessmentFinding>(ordered);
    }

    public CalibreBookId CalibreBookId { get; }
    public string Format { get; }
    public string ExpectedRelativePath { get; }
    public FormatFileFingerprint? ObservedFingerprint { get; }
    public AssessmentStatus Status { get; }
    public QualityScore? Score { get; }
    public AnalyzerVersion AnalyzerVersion { get; }
    public ScoringModelVersion ScoringModelVersion { get; }
    public EpubFeatureSummary Features { get; }
    public IReadOnlyList<AssessmentFinding> Findings { get; }

    private static int SeverityOrder(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Disqualifying => 0,
        FindingSeverity.Error => 1,
        FindingSeverity.Warning => 2,
        FindingSeverity.Information => 3,
        FindingSeverity.Positive => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(severity)),
    };
}
