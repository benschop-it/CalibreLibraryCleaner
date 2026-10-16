using System.Text;
using System.Text.RegularExpressions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Metadata;

public static partial class AuthorPseudonymAnnotationPolicy
{
    public const string PolicyVersion = "author-pseudonym-annotation/1.0.0";

    public static string ForMatching(string value) => TryRemove(value, out string? cleaned) ? cleaned! : value;

    public static bool ContainsMarker(string? value) => value is { Length: > 0 and <= 512 }
        && AnyMarker().IsMatch(value);

    public static bool TryNormalizeBook(IReadOnlyList<BookAuthor> authors, out IReadOnlyList<string> displayNames)
    {
        ArgumentNullException.ThrowIfNull(authors);
        List<string> names = [];
        bool changed = false;
        foreach (BookAuthor author in authors)
        {
            if (!TryRemove(author.Name, out string? cleaned))
            {
                names.Add(author.Name);
                continue;
            }
            changed = true;
            if (PipeAuthorNameNormalizationPolicy.TryNormalizeSortForm(cleaned, out PipeAuthorNameNormalization? normalized)
                || PipeAuthorNameNormalizationPolicy.TryNormalize(cleaned, out normalized))
                names.Add(normalized!.DisplayName);
            else
                names.Add(cleaned!);
        }
        displayNames = changed && names.Count > 0
            && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count ? names : [];
        return displayNames.Count > 0;
    }

    public static bool TryRemove(string? value, out string? cleaned)
    {
        cleaned = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)
            || !ContainsMarker(value)) return false;
        string original;
        try
        {
            original = value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return false;
        }

        StringBuilder result = new(original.Length);
        bool removed = false;
        for (int index = 0; index < original.Length; index++)
        {
            char character = original[index];
            if (character is ')' or ']') return false;
            if (character is not '(' and not '[')
            {
                result.Append(character);
                continue;
            }

            char closing = character == '(' ? ')' : ']';
            int end = original.IndexOf(closing, index + 1);
            if (end < 0) return false;
            string annotation = original[(index + 1)..end];
            if (annotation.IndexOfAny(['(', ')', '[', ']']) >= 0) return false;
            if (BracketMarker().IsMatch(annotation.Trim()))
            {
                removed = true;
                result.Append(' ');
            }
            else
            {
                result.Append(original.AsSpan(index, end - index + 1));
            }
            index = end;
        }

        string candidate = string.Join(' ', result.ToString().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Match suffix = BareSuffix().Match(candidate);
        if (suffix.Success)
        {
            removed = true;
            candidate = candidate[..suffix.Index].TrimEnd();
        }
        if (!removed || !candidate.Any(char.IsLetter)
            || string.Equals(candidate, value, StringComparison.Ordinal)) return false;
        cleaned = candidate;
        return true;
    }

    [GeneratedRegex(@"\A(?:is\s+)?(?:pseud\.?|pseudoniem|pseudonym)(?=\s|\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex BracketMarker();

    [GeneratedRegex(@"\s+(?:is\s+)?(?:pseud\.?|pseudoniem|pseudonym)(?=\s|\z)(?:\s+van\s+[^()\[\]]+)?\s*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex BareSuffix();

    [GeneratedRegex(@"(?:\A|[\s(\[])(?:is\s+)?(?:pseud\.?|pseudoniem|pseudonym)(?=\s|[\])]|\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 50)]
    private static partial Regex AnyMarker();
}
