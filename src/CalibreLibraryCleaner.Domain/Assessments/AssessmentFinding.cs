using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Findings;

namespace CalibreLibraryCleaner.Domain.Assessments;

public sealed record AssessmentFinding
{
    public const int MaximumEvidenceItems = 100;
    public const int MaximumEvidenceLength = 512;

    public AssessmentFinding(
        string ruleId,
        FindingSeverity severity,
        int scoreAdjustment,
        string explanation,
        IReadOnlyDictionary<string, string>? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        if (severity == FindingSeverity.Disqualifying && scoreAdjustment != 0)
        {
            throw new ArgumentException("Disqualifying findings cannot carry a numeric adjustment.", nameof(scoreAdjustment));
        }

        Dictionary<string, string> safeEvidence = new(StringComparer.Ordinal);
        foreach ((string key, string value) in evidence ?? new Dictionary<string, string>())
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            if (safeEvidence.Count >= MaximumEvidenceItems || key.Length > MaximumEvidenceLength || value.Length > MaximumEvidenceLength)
            {
                throw new ArgumentException("Assessment evidence exceeds its presentation-safe bounds.", nameof(evidence));
            }

            safeEvidence.Add(key, value);
        }

        RuleId = ruleId.Trim();
        Severity = severity;
        ScoreAdjustment = scoreAdjustment;
        Explanation = explanation.Trim();
        Evidence = new ReadOnlyDictionary<string, string>(safeEvidence);
    }

    public string RuleId { get; }

    public FindingSeverity Severity { get; }

    public int ScoreAdjustment { get; }

    public string Explanation { get; }

    public IReadOnlyDictionary<string, string> Evidence { get; }

    internal string EvidenceKey => string.Join(
        "\u001f",
        Evidence.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
}
