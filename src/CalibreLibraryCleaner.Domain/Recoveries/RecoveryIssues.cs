using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Recoveries;

public enum RecoveryIssueSeverity
{
    Blocking,
    AcknowledgementRequired,
    Information,
}

public sealed record RecoveryIssue
{
    public RecoveryIssue(
        string code,
        RecoveryIssueSeverity severity,
        string subject,
        string explanation,
        LogicalRecoveryRecordId? logicalRecordId = null,
        CalibreBookId? currentRecordId = null,
        string? format = null,
        IReadOnlyDictionary<string, string>? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        if (!Enum.IsDefined(severity)) throw new ArgumentOutOfRangeException(nameof(severity));
        if (code.Length > 128 || subject.Length > 256 || explanation.Length > 2048)
            throw new ArgumentException("Recovery issue text exceeds its bound.");
        KeyValuePair<string, string>[] orderedEvidence = (evidence ?? new Dictionary<string, string>())
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        if (orderedEvidence.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128
                || pair.Value is null || pair.Value.Length > 1024))
            throw new ArgumentException("Recovery issue evidence is invalid.", nameof(evidence));

        Code = code.Trim();
        Severity = severity;
        Subject = subject.Trim();
        Explanation = explanation.Trim();
        LogicalRecordId = logicalRecordId;
        CurrentRecordId = currentRecordId;
        Format = string.IsNullOrWhiteSpace(format) ? null : format.Trim().ToUpperInvariant();
        Evidence = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            orderedEvidence.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }

    public string Code { get; }
    public RecoveryIssueSeverity Severity { get; }
    public string Subject { get; }
    public string Explanation { get; }
    public LogicalRecoveryRecordId? LogicalRecordId { get; }
    public CalibreBookId? CurrentRecordId { get; }
    public string? Format { get; }
    public IReadOnlyDictionary<string, string> Evidence { get; }
}

public sealed record RecoveryEligibilityResult
{
    public RecoveryEligibilityResult(IEnumerable<RecoveryIssue> issues, DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = Array.AsReadOnly(issues.Distinct().OrderBy(value => value.Severity)
            .ThenBy(value => value.Code, StringComparer.Ordinal)
            .ThenBy(value => value.Subject, StringComparer.Ordinal)
            .ThenBy(value => value.LogicalRecordId?.Value, StringComparer.Ordinal)
            .ThenBy(value => value.CurrentRecordId?.Value ?? 0)
            .ThenBy(value => value.Format, StringComparer.Ordinal).ToArray());
        EvaluatedAtUtc = evaluatedAtUtc.ToUniversalTime();
    }

    public IReadOnlyList<RecoveryIssue> Issues { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
    public bool IsEligible => Issues.All(value => value.Severity != RecoveryIssueSeverity.Blocking);
    public IReadOnlyList<RecoveryIssue> BlockingIssues => Issues.Where(value => value.Severity == RecoveryIssueSeverity.Blocking).ToArray();
    public IReadOnlyList<RecoveryIssue> AcknowledgementWarnings => Issues.Where(value => value.Severity == RecoveryIssueSeverity.AcknowledgementRequired).ToArray();
    public IReadOnlyList<RecoveryIssue> Information => Issues.Where(value => value.Severity == RecoveryIssueSeverity.Information).ToArray();
}
