using CalibreLibraryCleaner.Domain.Assessments;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class EpubAssessmentFindingRowViewModel(AssessmentFinding finding)
{
    public string Severity { get; } = finding.Severity.ToString();
    public string RuleId { get; } = finding.RuleId;
    public string Adjustment { get; } = finding.ScoreAdjustment > 0 ? $"+{finding.ScoreAdjustment}" : finding.ScoreAdjustment.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Explanation { get; } = finding.Explanation;
    public string Evidence { get; } = string.Join("; ", finding.Evidence.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}: {pair.Value}"));
}
