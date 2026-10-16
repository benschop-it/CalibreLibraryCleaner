using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CalibreLibraryCleaner.Domain.Duplicates;

namespace CalibreLibraryCleaner.Domain.Metadata;

public enum AuthorResolutionStatus
{
    Known,
    Suspect,
    Unresolved,
    Conflicting,
}

public static class AuthorSuspicionReasonCodes
{
    public const string Missing = "AUTHOR.MISSING";
    public const string Placeholder = "AUTHOR.PLACEHOLDER";
    public const string ParenthesizedNumber = "AUTHOR.PARENTHESIZED_NUMBER";
    public const string NumberPrefixed = "AUTHOR.NUMBER_PREFIXED";
    public const string MatchesTitle = "AUTHOR.MATCHES_TITLE";
    public const string ResemblesTitle = "AUTHOR.RESEMBLES_TITLE";
    public const string MalformedSeparator = "AUTHOR.MALFORMED_SEPARATOR";
    public const string ConservativeNameOrder = "AUTHOR.CONSERVATIVE_NAME_ORDER";
    public const string DisplayTypography = "AUTHOR.DISPLAY_TYPOGRAPHY";
    public const string PseudonymAnnotation = "AUTHOR.PSEUDONYM_ANNOTATION";
}

public sealed record AuthorSuspicionAssessment
{
    public AuthorSuspicionAssessment(
        AuthorResolutionStatus status,
        IEnumerable<string> reasonCodes)
    {
        ArgumentNullException.ThrowIfNull(reasonCodes);
        string[] reasons = reasonCodes.Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!Enum.IsDefined(status)
            || reasons.Any(value => string.IsNullOrWhiteSpace(value)
                || value.Length > 128
                || value.Any(char.IsControl))
            || status == AuthorResolutionStatus.Known && reasons.Length != 0
            || status != AuthorResolutionStatus.Known && reasons.Length == 0)
            throw new ArgumentException("Author suspicion assessment is invalid.");
        Status = status;
        ReasonCodes = new ReadOnlyCollection<string>(reasons);
    }

    public AuthorResolutionStatus Status { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
}

public static class AuthorSuspicionPolicy
{
    public const string PolicyVersion = "author-suspicion/1.1.0";

    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "-",
        "?",
        "N/A",
        "NA",
        "NONE",
        "NULL",
        "UNKNOWN",
        "UNKNOWN AUTHOR",
        "AUTHOR UNKNOWN",
    };

    public static AuthorSuspicionAssessment Assess(
        string? title,
        IEnumerable<string?> authors)
    {
        ArgumentNullException.ThrowIfNull(authors);
        string?[] values = authors.ToArray();
        HashSet<string> reasons = new(StringComparer.Ordinal);
        bool unresolved = values.Length == 0;
        if (unresolved) reasons.Add(AuthorSuspicionReasonCodes.Missing);

        string[] titleTokens = Tokens(title);
        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                unresolved = true;
                reasons.Add(AuthorSuspicionReasonCodes.Missing);
                continue;
            }

            string author = value.Trim();
            if (IsPlaceholder(author))
            {
                unresolved = true;
                reasons.Add(AuthorSuspicionReasonCodes.Placeholder);
            }
            if (StartsWithNumber(author)) reasons.Add(AuthorSuspicionReasonCodes.NumberPrefixed);
            if (ContainsParenthesizedNumber(author))
                reasons.Add(AuthorSuspicionReasonCodes.ParenthesizedNumber);
            if (HasMalformedSeparator(author))
                reasons.Add(AuthorSuspicionReasonCodes.MalformedSeparator);
            if (AuthorPseudonymAnnotationPolicy.ContainsMarker(author))
                reasons.Add(AuthorSuspicionReasonCodes.PseudonymAnnotation);

            if (MetadataTextNormalizer.TryNormalizeTitle(title, out NormalizedTitle? normalizedTitle)
                && MetadataTextNormalizer.TryNormalizeAuthorName(author, out NormalizedAuthorName? normalizedAuthor)
                && string.Equals(normalizedTitle!.Value, normalizedAuthor!.Value, StringComparison.Ordinal))
            {
                reasons.Add(AuthorSuspicionReasonCodes.MatchesTitle);
            }
            else if (StronglyResemblesTitle(titleTokens, Tokens(author)))
            {
                reasons.Add(AuthorSuspicionReasonCodes.ResemblesTitle);
            }
        }

        AuthorResolutionStatus status = unresolved
            ? AuthorResolutionStatus.Unresolved
            : reasons.Count == 0
                ? AuthorResolutionStatus.Known
                : AuthorResolutionStatus.Suspect;
        return new(status, reasons);
    }

    private static bool IsPlaceholder(string value) =>
        MetadataTextNormalizer.TryNormalizeAuthorName(value, out NormalizedAuthorName? normalized)
        && Placeholders.Contains(normalized!.Value);

    private static bool StartsWithNumber(string value)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            return Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber;
        }
        return false;
    }

    private static bool ContainsParenthesizedNumber(string value)
    {
        for (int open = value.IndexOf('('); open >= 0; open = value.IndexOf('(', open + 1))
        {
            int close = value.IndexOf(')', open + 1);
            if (close < 0) return false;
            ReadOnlySpan<char> token = value.AsSpan(open + 1, close - open - 1).Trim();
            if (token.Length is >= 2 and <= 8 && token.ToString().All(char.IsDigit)) return true;
        }
        return false;
    }

    private static bool HasMalformedSeparator(string value)
    {
        int pipeCount = value.Count(character => character == '|');
        if (pipeCount > 0)
        {
            int separator = value.IndexOf('|');
            if (pipeCount != 1
                || string.IsNullOrWhiteSpace(value[..separator])
                || string.IsNullOrWhiteSpace(value[(separator + 1)..]))
                return true;
        }
        return value.Contains(';', StringComparison.Ordinal)
            || pipeCount == 1 && value.Contains(',', StringComparison.Ordinal);
    }

    private static bool StronglyResemblesTitle(
        string[] titleTokens,
        string[] authorTokens)
    {
        if (titleTokens.Length < 3 || authorTokens.Length < 3) return false;
        HashSet<string> titleSet = titleTokens.ToHashSet(StringComparer.Ordinal);
        HashSet<string> authorSet = authorTokens.ToHashSet(StringComparer.Ordinal);
        int intersection = titleSet.Intersect(authorSet, StringComparer.Ordinal).Count();
        int minimum = Math.Min(titleSet.Count, authorSet.Count);
        int maximum = Math.Max(titleSet.Count, authorSet.Count);
        return intersection >= 3
            && intersection * 100 >= minimum * 80
            && intersection * 100 >= maximum * 60;
    }

    private static string[] Tokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        List<string> tokens = [];
        StringBuilder token = new();
        foreach (Rune rune in value.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                token.Append(rune.ToString().ToUpperInvariant());
            }
            else if (token.Length > 0)
            {
                tokens.Add(token.ToString());
                token.Clear();
            }
        }
        if (token.Length > 0) tokens.Add(token.ToString());
        return tokens.ToArray();
    }
}
