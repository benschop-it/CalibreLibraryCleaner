using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Metadata;

public static partial class AuthorDisplayNameHarmonizationPolicy
{
    public const string PolicyVersion = "author-display-name-harmonization/1.0.0";

    public static bool TryHarmonizeBook(
        IReadOnlyList<BookAuthor> authors,
        out IReadOnlyList<string> displayNames,
        out IReadOnlyList<string> sortNames)
    {
        ArgumentNullException.ThrowIfNull(authors);
        List<string> displays = [];
        List<string> sorts = [];
        bool changed = false;
        foreach (BookAuthor author in authors)
        {
            if (ContainsQualifierDelimiters(author.Name)
                || ContainsQualifierDelimiters(author.SortName))
            {
                displayNames = [];
                sortNames = [];
                return false;
            }
            string display = NormalizeDisplay(author.Name);
            string sort = NormalizeSort(author.SortName);
            if (display.Length == 0 || sort.Length == 0)
            {
                displayNames = [];
                sortNames = [];
                return false;
            }
            displays.Add(display);
            sorts.Add(sort);
            changed |= display != author.Name || sort != author.SortName;
        }
        if (!changed || displays.Count == 0
            || displays.Distinct(StringComparer.Ordinal).Count() != displays.Count)
        {
            displayNames = [];
            sortNames = [];
            return false;
        }
        displayNames = displays.ToArray();
        sortNames = sorts.ToArray();
        return true;
    }

    private static string NormalizeDisplay(string value) => NormalizeInitialPrefix(NormalizeBase(value));

    private static string NormalizeSort(string value)
    {
        string normalized = NormalizeBase(value);
        int comma = normalized.IndexOf(',', StringComparison.Ordinal);
        if (comma <= 0 || comma != normalized.LastIndexOf(',') || comma == normalized.Length - 1)
            return NormalizeInitialPrefix(normalized);
        string given = normalized[(comma + 1)..].Trim();
        string normalizedGiven = NormalizeInitialPrefix(given, requireFollowingName: false);
        return normalizedGiven == given
            ? normalized
            : $"{normalized[..comma].Trim()}, {normalizedGiven}";
    }

    private static string NormalizeBase(string value)
    {
        string normalized;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        StringBuilder result = new(normalized.Length);
        bool pendingSpace = false;
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = result.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }
            result.Append(rune.Value is 0x2018 or 0x2019 or 0x02BC ? "'" : rune.ToString());
        }
        return SingleLetterApostropheSpace().Replace(result.ToString(), "$1'");
    }

    private static string NormalizeInitialPrefix(
        string value,
        bool requireFollowingName = true)
    {
        string prepared = InitialBeforeSeparatedDot().Replace(value, "$1.");
        string[] tokens = prepared.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.Length > 4
            || requireFollowingName && tokens.Length < 2) return value;
        List<string> initials = [];
        int used = 0;
        bool hadPeriod = false;
        foreach (string token in tokens)
        {
            if (!TryParseInitialToken(token, out string[] parts, out bool tokenHadPeriod)) break;
            initials.AddRange(parts);
            hadPeriod |= tokenHadPeriod;
            used++;
        }
        if (initials.Count == 0 || requireFollowingName && used >= tokens.Length) return value;
        bool safe = hadPeriod
            || initials.Count >= 2
            || tokens.Length <= 3
            || used < tokens.Length && StartsWithLowercaseLetter(tokens[used]);
        if (!safe) return value;
        return string.Join(' ', initials.Select(CanonicalInitial)
            .Concat(tokens.Skip(used)));
    }

    private static bool TryParseInitialToken(
        string token,
        out string[] parts,
        out bool hadPeriod)
    {
        string collapsed = CollapseRepeatedPeriods(token);
        Rune[] bareRunes = collapsed.EnumerateRunes().ToArray();
        if (bareRunes.Length == 1 && Rune.IsLetter(bareRunes[0]))
        {
            parts = [collapsed];
            hadPeriod = false;
            return true;
        }
        hadPeriod = collapsed.Length > 0 && collapsed[^1] == '.';
        if (!hadPeriod)
        {
            parts = [];
            return false;
        }
        parts = collapsed[..^1].Split('.', StringSplitOptions.None);
        if (parts.Length == 0 || parts.Any(value => !AllLetters(value))) return false;
        if (parts.Length == 1) return RuneCount(parts[0]) == 1;
        return parts.Take(parts.Length - 1).All(value => RuneCount(value) == 1)
            && RuneCount(parts[^1]) is 1 or 2;
    }

    private static string CollapseRepeatedPeriods(string value)
    {
        StringBuilder result = new(value.Length);
        bool previousPeriod = false;
        foreach (char character in value)
        {
            if (character == '.' && previousPeriod) continue;
            result.Append(character);
            previousPeriod = character == '.';
        }
        return result.ToString();
    }

    private static string CanonicalInitial(string value)
    {
        Rune[] runes = value.EnumerateRunes().ToArray();
        StringBuilder result = new(value.Length + 1);
        result.Append(runes[0].ToString().ToUpperInvariant());
        foreach (Rune rune in runes.Skip(1)) result.Append(rune.ToString().ToLowerInvariant());
        return result.Append('.').ToString();
    }

    private static bool StartsWithLowercaseLetter(string value)
    {
        Rune first = value.EnumerateRunes().First();
        return Rune.GetUnicodeCategory(first) == UnicodeCategory.LowercaseLetter;
    }

    private static bool AllLetters(string value) => value.Length > 0
        && value.EnumerateRunes().All(Rune.IsLetter);

    private static int RuneCount(string value) => value.EnumerateRunes().Count();

    private static bool ContainsQualifierDelimiters(string value) => value.IndexOfAny(['(', ')', '[', ']', '{', '}']) >= 0;

    [GeneratedRegex(@"\b(\p{L})['’ʼ]\s+(?=\p{L})", RegexOptions.CultureInvariant)]
    private static partial Regex SingleLetterApostropheSpace();

    [GeneratedRegex(@"^(\p{L})\s+\.", RegexOptions.CultureInvariant)]
    private static partial Regex InitialBeforeSeparatedDot();
}
