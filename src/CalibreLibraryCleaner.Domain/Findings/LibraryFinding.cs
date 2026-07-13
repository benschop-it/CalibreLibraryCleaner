using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Findings;

public sealed record LibraryFinding
{
    public LibraryFinding(
        string code,
        FindingSeverity severity,
        string message,
        string suggestedAction,
        CalibreBookId? bookId = null,
        string? format = null,
        string? relativePath = null,
        IReadOnlyDictionary<string, string>? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedAction);

        Code = code;
        Severity = severity;
        Message = message;
        SuggestedAction = suggestedAction;
        BookId = bookId;
        Format = format;
        RelativePath = relativePath;
        Evidence = new ReadOnlyDictionary<string, string>(
            evidence is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(evidence, StringComparer.Ordinal));
    }

    public string Code { get; }

    public FindingSeverity Severity { get; }

    public string Message { get; }

    public string SuggestedAction { get; }

    public CalibreBookId? BookId { get; }

    public string? Format { get; }

    public string? RelativePath { get; }

    public IReadOnlyDictionary<string, string> Evidence { get; }
}
