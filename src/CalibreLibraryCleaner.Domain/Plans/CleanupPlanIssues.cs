using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Plans;

public enum CleanupPlanIssueSeverity
{
    BlockingError,
    Warning,
    Information,
}

public enum CleanupPlanIssueSubjectKind
{
    Plan,
    Library,
    Group,
    Record,
    Format,
    Assessment,
    Backup,
    Approval,
    Provenance,
}

public sealed record CleanupPlanIssue
{
    public CleanupPlanIssue(
        string code,
        CleanupPlanIssueSeverity severity,
        CleanupPlanIssueSubjectKind subjectKind,
        string explanation,
        CalibreBookId? recordId = null,
        string? format = null,
        IReadOnlyDictionary<string, string>? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        if (!Enum.IsDefined(severity)) throw new ArgumentOutOfRangeException(nameof(severity));
        if (!Enum.IsDefined(subjectKind)) throw new ArgumentOutOfRangeException(nameof(subjectKind));
        if (code.Length > 128 || explanation.Length > 1024) throw new ArgumentException("Cleanup plan issue text exceeds its bounds.");
        Code = code.Trim();
        Severity = severity;
        SubjectKind = subjectKind;
        Explanation = explanation.Trim();
        RecordId = recordId;
        Format = string.IsNullOrWhiteSpace(format) ? null : format.ToUpperInvariant();
        Dictionary<string, string> ordered = new(StringComparer.Ordinal);
        foreach ((string key, string value) in (evidence ?? new Dictionary<string, string>())
                     .OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            if (ordered.Count >= 100 || string.IsNullOrWhiteSpace(key) || value is null || key.Length > 512 || value.Length > 512)
                throw new ArgumentException("Cleanup plan issue evidence exceeds its bounds.", nameof(evidence));
            ordered.Add(key, value);
        }

        Evidence = new ReadOnlyDictionary<string, string>(ordered);
    }

    public string Code { get; }
    public CleanupPlanIssueSeverity Severity { get; }
    public CleanupPlanIssueSubjectKind SubjectKind { get; }
    public string Explanation { get; }
    public CalibreBookId? RecordId { get; }
    public string? Format { get; }
    public IReadOnlyDictionary<string, string> Evidence { get; }
}

public sealed record CleanupPlanValidationResult
{
    public CleanupPlanValidationResult(
        IEnumerable<CleanupPlanIssue> issues,
        DateTimeOffset validatedAtUtc,
        CleanupPlanInputIdentity? validatedInputIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = Array.AsReadOnly(issues
            .OrderBy(value => value.Severity)
            .ThenBy(value => value.RecordId?.Value ?? 0)
            .ThenBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.Code, StringComparer.Ordinal)
            .ThenBy(value => value.Explanation, StringComparer.Ordinal)
            .ToArray());
        ValidatedAtUtc = validatedAtUtc.ToUniversalTime();
        ValidatedInputIdentity = validatedInputIdentity;
    }

    public IReadOnlyList<CleanupPlanIssue> Issues { get; }
    public DateTimeOffset ValidatedAtUtc { get; }
    public CleanupPlanInputIdentity? ValidatedInputIdentity { get; }
    public bool IsValid => Issues.All(value => value.Severity != CleanupPlanIssueSeverity.BlockingError);
    public IReadOnlyList<CleanupPlanIssue> BlockingErrors => Issues.Where(value => value.Severity == CleanupPlanIssueSeverity.BlockingError).ToArray();
    public IReadOnlyList<CleanupPlanIssue> Warnings => Issues.Where(value => value.Severity == CleanupPlanIssueSeverity.Warning).ToArray();
    public IReadOnlyList<CleanupPlanIssue> Information => Issues.Where(value => value.Severity == CleanupPlanIssueSeverity.Information).ToArray();
}
