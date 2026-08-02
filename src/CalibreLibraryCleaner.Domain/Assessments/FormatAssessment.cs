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
        IEnumerable<AssessmentFinding> findings,
        IEnumerable<AssessmentScoreComponent>? scoreComponents = null,
        FormatFileObservation? observedObservation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        string canonicalFormat = format.Trim().ToUpperInvariant();
        if (canonicalFormat.Length > 16 || canonicalFormat.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException("The assessment format must be a bounded canonical token.", nameof(format));
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
        ArgumentNullException.ThrowIfNull(findings);
        if (observedFingerprint is not null && observedObservation is not null
            && observedFingerprint.SizeInBytes != observedObservation.Length)
        {
            throw new ArgumentException("The assessment fingerprint and verified observation lengths must agree.", nameof(observedObservation));
        }
        AssessmentFinding[] orderedFindings = findings
            .OrderBy(finding => SeverityOrder(finding.Severity))
            .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
            .ThenBy(finding => finding.ScoreComponentId.Value, StringComparer.Ordinal)
            .ThenBy(finding => finding.EvidenceKey, StringComparer.Ordinal)
            .ThenBy(finding => finding.Explanation, StringComparer.Ordinal)
            .ToArray();
        if (orderedFindings.Length == 0)
        {
            throw new ArgumentException("An assessment requires findings.", nameof(findings));
        }

        AssessmentScoreComponent[] declaredComponents = (scoreComponents ?? [new(AssessmentScoreComponentId.Overall, 100)])
            .OrderBy(component => component.Id.Value, StringComparer.Ordinal)
            .ToArray();
        if (declaredComponents.Length == 0
            || declaredComponents.Select(component => component.Id).Distinct().Count() != declaredComponents.Length
            || declaredComponents.Sum(component => component.MaximumScore) != 100)
        {
            throw new ArgumentException("Score components must be unique and sum to 100.", nameof(scoreComponents));
        }

        HashSet<AssessmentScoreComponentId> declaredIds = declaredComponents.Select(component => component.Id).ToHashSet();
        if (orderedFindings.Any(finding => finding.ScoreAdjustment != 0 && !declaredIds.Contains(finding.ScoreComponentId)))
        {
            throw new ArgumentException("Every nonzero finding must belong to one declared score component.", nameof(findings));
        }

        AssessmentScoreComponentResult[] componentResults = declaredComponents.Select(component =>
        {
            int raw = orderedFindings
                .Where(finding => finding.Severity != FindingSeverity.Disqualifying && finding.ScoreComponentId == component.Id)
                .Sum(finding => finding.ScoreAdjustment);
            return new AssessmentScoreComponentResult(component.Id, component.MaximumScore, raw, Math.Clamp(raw, 0, component.MaximumScore));
        }).ToArray();

        bool hasDisqualifier = orderedFindings.Any(finding => finding.Severity == FindingSeverity.Disqualifying);
        if (status == AssessmentStatus.Disqualified)
        {
            if (!hasDisqualifier || score is not null)
            {
                throw new ArgumentException("A disqualified assessment requires a disqualifier and no score.", nameof(status));
            }
        }
        else if (status == AssessmentStatus.Unassessed)
        {
            if (hasDisqualifier || score is not null || orderedFindings.Any(finding => finding.ScoreAdjustment != 0))
            {
                throw new ArgumentException("An unassessed result requires zero-point non-disqualifying findings and no score.", nameof(status));
            }
        }
        else
        {
            int expectedScore = componentResults.Sum(component => component.Score);
            if (hasDisqualifier || score is null || score.Value.Value != expectedScore)
            {
                throw new ArgumentException("A completed score must be derived entirely from its findings and components.", nameof(score));
            }
        }

        CalibreBookId = calibreBookId;
        Format = canonicalFormat;
        ExpectedRelativePath = normalizedPath;
        ObservedFingerprint = observedFingerprint;
        ObservedObservation = observedObservation;
        Status = status;
        Score = score;
        AnalyzerVersion = analyzerVersion;
        ScoringModelVersion = scoringModelVersion;
        Findings = new ReadOnlyCollection<AssessmentFinding>(orderedFindings);
        ScoreComponents = new ReadOnlyCollection<AssessmentScoreComponentResult>(componentResults);
    }

    public CalibreBookId CalibreBookId { get; }
    public string Format { get; }
    public string ExpectedRelativePath { get; }
    public FormatFileFingerprint? ObservedFingerprint { get; }
    public FormatFileObservation? ObservedObservation { get; }
    public AssessmentStatus Status { get; }
    public QualityScore? Score { get; }
    public AnalyzerVersion AnalyzerVersion { get; }
    public ScoringModelVersion ScoringModelVersion { get; }
    public IReadOnlyList<AssessmentFinding> Findings { get; }
    public IReadOnlyList<AssessmentScoreComponentResult> ScoreComponents { get; }

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
