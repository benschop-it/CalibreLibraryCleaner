using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Recommendations;

public sealed record RecommendationReason
{
    public RecommendationReason(
        string code,
        RecommendationSubjectKind subjectKind,
        string explanation,
        CalibreBookId? bookId = null,
        string? format = null,
        IReadOnlyDictionary<string, string>? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        Code = code.Trim();
        SubjectKind = subjectKind;
        Explanation = explanation.Trim();
        BookId = bookId;
        Format = string.IsNullOrWhiteSpace(format) ? null : format.ToUpperInvariant();
        Evidence = OrderedEvidence(evidence);
    }

    public string Code { get; }
    public RecommendationSubjectKind SubjectKind { get; }
    public string Explanation { get; }
    public CalibreBookId? BookId { get; }
    public string? Format { get; }
    public IReadOnlyDictionary<string, string> Evidence { get; }

    private static ReadOnlyDictionary<string, string> OrderedEvidence(IReadOnlyDictionary<string, string>? evidence)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach ((string key, string value) in (evidence ?? new Dictionary<string, string>())
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            if (result.Count >= 100 || key.Length > 512 || value.Length > 512)
            {
                throw new ArgumentException("Recommendation evidence exceeds its presentation-safe bounds.", nameof(evidence));
            }

            result.Add(key, value);
        }

        return new ReadOnlyDictionary<string, string>(result);
    }
}

public sealed record RecommendationWarning
{
    public RecommendationWarning(
        string code,
        RecommendationWarningSeverity severity,
        RecommendationSubjectKind subjectKind,
        string explanation,
        CalibreBookId? bookId = null,
        string? format = null,
        IReadOnlyDictionary<string, string>? evidence = null)
    {
        RecommendationReason value = new(code, subjectKind, explanation, bookId, format, evidence);
        Code = value.Code;
        Severity = severity;
        SubjectKind = value.SubjectKind;
        Explanation = value.Explanation;
        BookId = value.BookId;
        Format = value.Format;
        Evidence = value.Evidence;
    }

    public string Code { get; }
    public RecommendationWarningSeverity Severity { get; }
    public RecommendationSubjectKind SubjectKind { get; }
    public string Explanation { get; }
    public CalibreBookId? BookId { get; }
    public string? Format { get; }
    public IReadOnlyDictionary<string, string> Evidence { get; }
}
