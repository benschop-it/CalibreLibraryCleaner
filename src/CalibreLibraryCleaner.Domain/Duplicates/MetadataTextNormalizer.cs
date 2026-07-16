using System.Globalization;
using System.Text;

namespace CalibreLibraryCleaner.Domain.Duplicates;

public static class MetadataTextNormalizer
{
    public static bool TryNormalizeTitle(string? value, out NormalizedTitle? normalizedTitle)
    {
        string? normalized = Normalize(value);
        normalizedTitle = normalized is null ? null : new NormalizedTitle(normalized);
        return normalizedTitle is not null;
    }

    public static bool TryNormalizeAuthorName(string? value, out NormalizedAuthorName? normalizedAuthorName)
    {
        string? normalized = Normalize(value);
        normalizedAuthorName = normalized is null ? null : new NormalizedAuthorName(normalized);
        return normalizedAuthorName is not null;
    }

    public static bool TryCreateAuthorSet(
        IEnumerable<string?> authorNames,
        out NormalizedAuthorSet? normalizedAuthorSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorNames);
        List<NormalizedAuthorName> names = [];
        foreach (string? authorName in authorNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryNormalizeAuthorName(authorName, out NormalizedAuthorName? normalizedAuthorName))
            {
                normalizedAuthorSet = null;
                return false;
            }

            names.Add(normalizedAuthorName!);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (names.Count == 0)
        {
            normalizedAuthorSet = null;
            return false;
        }

        normalizedAuthorSet = new(names);
        return true;
    }

    private static string? Normalize(string? value)
    {
        if (value is null || !IsWellFormedUtf16(value))
        {
            return null;
        }

        string cased;
        try
        {
            cased = value
                .Normalize(NormalizationForm.FormC)
                .ToUpperInvariant()
                .Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return null;
        }
        StringBuilder collapsed = new(cased.Length);
        bool pendingWhitespace = false;
        foreach (Rune rune in cased.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Format)
            {
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                pendingWhitespace = collapsed.Length > 0;
                continue;
            }

            if (pendingWhitespace)
            {
                collapsed.Append(' ');
                pendingWhitespace = false;
            }

            collapsed.Append(rune.ToString());
        }

        Rune[] runes = collapsed.ToString().EnumerateRunes().ToArray();
        StringBuilder punctuationSpaced = new(collapsed.Length);
        for (int index = 0; index < runes.Length; index++)
        {
            Rune rune = runes[index];
            if (rune.Value == ' ' &&
                ((index > 0 && IsPunctuation(runes[index - 1])) ||
                 (index + 1 < runes.Length && IsPunctuation(runes[index + 1]))))
            {
                continue;
            }

            punctuationSpaced.Append(rune.ToString());
        }

        string normalized = punctuationSpaced.ToString().Normalize(NormalizationForm.FormC);
        return normalized.Length == 0 ? null : normalized;
    }

    private static bool IsPunctuation(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.ConnectorPunctuation or
        UnicodeCategory.DashPunctuation or
        UnicodeCategory.OpenPunctuation or
        UnicodeCategory.ClosePunctuation or
        UnicodeCategory.InitialQuotePunctuation or
        UnicodeCategory.FinalQuotePunctuation or
        UnicodeCategory.OtherPunctuation;

    private static bool IsWellFormedUtf16(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                return false;
            }
        }

        return true;
    }
}
