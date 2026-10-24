using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace CalibreLibraryCleaner.Domain.Metadata;

public enum AuthorCreditRole
{
    Author,
    Editor,
    Translator,
    Illustrator,
    ForewordWriter,
    Publisher,
    Dedication,
}

public sealed record AuthorBylineCandidate
{
    public AuthorBylineCandidate(
        AuthorCreditRole role,
        IEnumerable<string> names,
        string marker,
        int lineNumber)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineNumber);
        string[] values = names.ToArray();
        if (!Enum.IsDefined(role)
            || values.Length is < 1 or > 4
            || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128)
            || marker.Length > 32)
            throw new ArgumentException("Author byline candidate is invalid.");
        Role = role;
        Names = new ReadOnlyCollection<string>(values);
        Marker = marker;
        LineNumber = lineNumber;
    }

    public AuthorCreditRole Role { get; }
    public IReadOnlyList<string> Names { get; }
    public string Marker { get; }
    public int LineNumber { get; }
}

public static class AuthorBylinePolicy
{
    public const string PolicyVersion = "author-byline/1.1.0";
    public const int MaximumLines = 200;
    public const int MaximumLineCharacters = 512;

    private static readonly (string Marker, AuthorCreditRole Role)[] Markers =
    [
        ("translated by ", AuthorCreditRole.Translator),
        ("illustrated by ", AuthorCreditRole.Illustrator),
        ("foreword by ", AuthorCreditRole.ForewordWriter),
        ("introduction by ", AuthorCreditRole.ForewordWriter),
        ("published by ", AuthorCreditRole.Publisher),
        ("dedicated to ", AuthorCreditRole.Dedication),
        ("vertaald door ", AuthorCreditRole.Translator),
        ("traduit par ", AuthorCreditRole.Translator),
        ("traducido por ", AuthorCreditRole.Translator),
        ("geschreven door ", AuthorCreditRole.Author),
        ("verfasst von ", AuthorCreditRole.Author),
        ("auteur: ", AuthorCreditRole.Author),
        ("autor: ", AuthorCreditRole.Author),
        ("autora: ", AuthorCreditRole.Author),
        ("translator: ", AuthorCreditRole.Translator),
        ("written by ", AuthorCreditRole.Author),
        ("edited by ", AuthorCreditRole.Editor),
        ("author: ", AuthorCreditRole.Author),
        ("editor: ", AuthorCreditRole.Editor),
        ("by ", AuthorCreditRole.Author),
    ];

    private static readonly HashSet<string> LowercaseParticles = new(StringComparer.OrdinalIgnoreCase)
    {
        "al", "bin", "da", "de", "del", "der", "di", "dos", "du", "la", "le",
        "ten", "ter", "van", "von",
    };

    private static readonly HashSet<string> RejectedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "edited", "editor", "illustrated", "introduction", "preface", "translated",
        "translator", "with",
    };

    public static IReadOnlyList<AuthorBylineCandidate> Extract(IEnumerable<string?> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        List<AuthorBylineCandidate> candidates = [];
        int lineNumber = 0;
        foreach (string? source in lines.Take(MaximumLines))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(source)) continue;
            string line = Normalize(source);
            if (line.Length == 0 || line.Length > MaximumLineCharacters) continue;
            foreach ((string marker, AuthorCreditRole role) in Markers)
            {
                if (!line.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
                string value = line[marker.Length..].Trim();
                string[] names = SplitNames(value);
                if (names.Length > 0 && names.All(IsNameLike))
                    candidates.Add(new(role, names, marker.Trim(), lineNumber));
                break;
            }
        }
        return new ReadOnlyCollection<AuthorBylineCandidate>(candidates);
    }

    private static string[] SplitNames(string value)
    {
        if (value.Length is < 2 or > 512 || value.Contains(';', StringComparison.Ordinal)) return [];
        string canonical = value.Replace(" & ", " and ", StringComparison.Ordinal);
        string[] names = canonical.Split(
            " and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length is >= 1 and <= 4 ? names : [];
    }

    private static bool IsNameLike(string value)
    {
        if (value.Length is < 2 or > 128
            || value.Any(char.IsDigit)
            || value.IndexOfAny([':', ';', '!', '?', '(', ')', '[', ']']) >= 0)
            return false;
        string[] tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is < 1 or > 8 || tokens.Any(token => RejectedWords.Contains(TrimPunctuation(token))))
            return false;
        bool letter = false;
        foreach (string token in tokens)
        {
            string word = TrimPunctuation(token);
            if (word.Length == 0 || word.Length > 64) return false;
            Rune? firstLetter = null;
            foreach (Rune rune in word.EnumerateRunes())
            {
                if (!Rune.IsLetter(rune)) continue;
                firstLetter = rune;
                break;
            }
            if (firstLetter is null) return false;
            letter = true;
            UnicodeCategory category = Rune.GetUnicodeCategory(firstLetter.Value);
            bool cased = category is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter;
            if (cased && category == UnicodeCategory.LowercaseLetter
                && !LowercaseParticles.Contains(word))
                return false;
        }
        return letter;
    }

    private static string TrimPunctuation(string value) => value.Trim('.', ',', '\'', '’', '-');

    private static string Normalize(string value) => string.Join(' ',
        value.Normalize(NormalizationForm.FormC).Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
