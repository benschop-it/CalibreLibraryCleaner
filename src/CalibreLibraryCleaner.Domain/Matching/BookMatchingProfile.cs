using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Matching;

public sealed record BookMatchingProfile
{
    public BookMatchingProfile(
        CalibreBookId bookId,
        IEnumerable<string> titleKeys,
        IEnumerable<string> titleTokens,
        IEnumerable<string> authorKeys,
        IEnumerable<string> authorTokens,
        IEnumerable<string>? languages = null,
        IEnumerable<string>? strongIdentifiers = null,
        IEnumerable<string>? embeddedIdentifiers = null,
        IEnumerable<string>? exactBinaryKeys = null,
        string? seriesKey = null,
        decimal? seriesIndex = null,
        int? publicationYear = null)
    {
        BookId = bookId;
        TitleKeys = Copy(titleKeys, 8, 512);
        TitleTokens = Copy(titleTokens, 128, 128);
        AuthorKeys = Copy(authorKeys, 64, 256);
        AuthorTokens = Copy(authorTokens, 64, 128);
        Languages = Copy(languages, 16, 32);
        StrongIdentifiers = Copy(strongIdentifiers, 32, 256);
        EmbeddedIdentifiers = Copy(embeddedIdentifiers, 64, 256);
        ExactBinaryKeys = Copy(exactBinaryKeys, 64, 256);
        SeriesKey = Bound(seriesKey, 256);
        SeriesIndex = seriesIndex;
        PublicationYear = publicationYear;
        if (TitleKeys.Count == 0 || TitleTokens.Count == 0)
            throw new ArgumentException("A matching profile requires a usable title.");
    }

    public CalibreBookId BookId { get; }
    public IReadOnlyList<string> TitleKeys { get; }
    public IReadOnlyList<string> TitleTokens { get; }
    public IReadOnlyList<string> AuthorKeys { get; }
    public IReadOnlyList<string> AuthorTokens { get; }
    public IReadOnlyList<string> Languages { get; }
    public IReadOnlyList<string> StrongIdentifiers { get; }
    public IReadOnlyList<string> EmbeddedIdentifiers { get; }
    public IReadOnlyList<string> ExactBinaryKeys { get; }
    public string? SeriesKey { get; }
    public decimal? SeriesIndex { get; }
    public int? PublicationYear { get; }

    private static ReadOnlyCollection<string> Copy(
        IEnumerable<string>? values,
        int maximumCount,
        int maximumLength)
    {
        string[] result = (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(maximumCount + 1)
            .ToArray();
        if (result.Length > maximumCount || result.Any(value => value.Length > maximumLength))
            throw new ArgumentException("Matching profile evidence exceeds its bounds.", nameof(values));
        return new(result);
    }

    private static string? Bound(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string result = value.Trim();
        return result.Length <= maximumLength
            ? result
            : throw new ArgumentOutOfRangeException(nameof(value));
    }
}

public static class CandidateMetadataNormalizer
{
    private const string AuthorExactPrefix = "AUTHOR_EXACT:";
    private const string AuthorAliasPrefix = "AUTHOR_ALIAS:";
    private const string AuthorGivenPrefix = "AUTHOR_GIVEN:";
    private static readonly Dictionary<string, string> LanguageAliases = CreateLanguageAliases();
    private static readonly HashSet<string> EditionMarkers = new(StringComparer.Ordinal)
    {
        "ABRIDGED", "ANNOTATED", "EXPANDED", "ILLUSTRATED", "REVISED", "UNABRIDGED",
    };

    private static readonly HashSet<string> UnknownAuthorTokens = new(StringComparer.Ordinal)
    {
        "ANONIEM", "ANONYMOUS", "DESCONOCIDO", "INCONNU", "ONBEKEND", "UNKNOWN", "UNBEKANNT",
    };

    public static string[] TitleTokens(string value) => Tokenize(value);

    public static bool IsEditionMarker(string token) => EditionMarkers.Contains(token);

    public static int TitleSimilarityPermille(
        IEnumerable<string> first,
        IEnumerable<string> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        HashSet<string> left = first.Where(value => !IsEditionMarker(value)).ToHashSet(StringComparer.Ordinal);
        HashSet<string> right = second.Where(value => !IsEditionMarker(value)).ToHashSet(StringComparer.Ordinal);
        if (left.Count == 0 || right.Count == 0) return 0;
        int intersection = left.Count(right.Contains);
        int union = left.Count + right.Count - intersection;
        return union == 0 ? 0 : intersection * 1000 / union;
    }

    public static string[] TitleKeys(string value)
    {
        string[] tokens = Tokenize(value);
        if (tokens.Length == 0) return [];
        List<string> keys = [string.Join(' ', tokens)];
        if (tokens.Length >= 2 && IsOrdinalToken(tokens[0]))
            keys.Add(string.Join(' ', tokens.Skip(1)));
        return keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public static string[] AuthorTokens(IEnumerable<string> authors)
    {
        ArgumentNullException.ThrowIfNull(authors);
        return authors.SelectMany(Tokenize)
            .Where(value => !UnknownAuthorTokens.Contains(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static string[] AuthorKeys(IEnumerable<string> authors)
    {
        ArgumentNullException.ThrowIfNull(authors);
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (string author in authors)
        {
            int comma = author.IndexOf(',', StringComparison.Ordinal);
            string[] familyTokens;
            string[] givenTokens;
            if (comma >= 0)
            {
                familyTokens = Tokenize(author[..comma]);
                givenTokens = Tokenize(author[(comma + 1)..]);
            }
            else
            {
                string[] tokens = Tokenize(author);
                if (tokens.Length == 0) continue;
                familyTokens = [tokens[^1]];
                givenTokens = tokens[..^1];
            }
            if (familyTokens.Length == 0
                || familyTokens.All(UnknownAuthorTokens.Contains)
                || givenTokens.Length > 8)
                continue;
            string family = string.Join(' ', familyTokens);
            if (family.Length < 2) continue;
            string initials = givenTokens.Length == 0
                ? "-"
                : string.Concat(givenTokens.Select(value => value[0]));
            string alias = $"{family}|{initials}";
            keys.Add("FAMILY:" + family);
            keys.Add(AuthorAliasPrefix + alias);
            keys.Add(AuthorExactPrefix + family + "|" + string.Join(' ', givenTokens));
            for (int index = 0; index < givenTokens.Length; index++)
            {
                if (givenTokens[index].Length > 1)
                    keys.Add($"{AuthorGivenPrefix}{alias}|{index.ToString(CultureInfo.InvariantCulture)}|{givenTokens[index]}");
            }
        }
        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    public static string[] AuthorAliasKeys(IEnumerable<string> authorKeys)
    {
        ArgumentNullException.ThrowIfNull(authorKeys);
        return authorKeys.Where(value => value.StartsWith(AuthorAliasPrefix, StringComparison.Ordinal))
            .Select(value => value[AuthorAliasPrefix.Length..])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static bool HaveExactAuthorIdentity(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second) => first
        .Where(value => value.StartsWith(AuthorExactPrefix, StringComparison.Ordinal))
        .Intersect(
            second.Where(value => value.StartsWith(AuthorExactPrefix, StringComparison.Ordinal)),
            StringComparer.Ordinal)
        .Any();

    public static bool HaveCompatibleAuthorIdentity(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second)
    {
        string[] sharedAliases = AuthorAliasKeys(first)
            .Intersect(AuthorAliasKeys(second), StringComparer.Ordinal)
            .ToArray();
        return sharedAliases.Any(alias => GivenNamesCompatible(alias, first, second));
    }

    private static bool GivenNamesCompatible(
        string alias,
        IReadOnlyList<string> first,
        IReadOnlyList<string> second)
    {
        Dictionary<int, HashSet<string>> firstGiven = ExpandedGivenNames(alias, first);
        Dictionary<int, HashSet<string>> secondGiven = ExpandedGivenNames(alias, second);
        foreach (int position in firstGiven.Keys.Intersect(secondGiven.Keys))
        {
            if (!firstGiven[position].Overlaps(secondGiven[position])) return false;
        }
        return true;
    }

    private static Dictionary<int, HashSet<string>> ExpandedGivenNames(
        string alias,
        IReadOnlyList<string> keys)
    {
        string prefix = AuthorGivenPrefix + alias + "|";
        Dictionary<int, HashSet<string>> result = [];
        foreach (string key in keys.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)))
        {
            string[] parts = key[prefix.Length..].Split('|', 2);
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int position))
                continue;
            if (!result.TryGetValue(position, out HashSet<string>? names))
            {
                names = new(StringComparer.Ordinal);
                result.Add(position, names);
            }
            names.Add(parts[1]);
        }
        return result;
    }

    public static string? NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim().Replace('_', '-').ToLowerInvariant();
        int separator = normalized.IndexOf('-');
        string primary = separator < 0 ? normalized : normalized[..separator];
        if (primary is "und" or "unknown" or "mul" || primary.Length is < 2 or > 3) return null;
        return LanguageAliases.GetValueOrDefault(primary, primary);
    }

    private static Dictionary<string, string> CreateLanguageAliases()
    {
        Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase);
        foreach (CultureInfo culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            string twoLetter = culture.TwoLetterISOLanguageName.ToLowerInvariant();
            if (twoLetter.Length != 2) continue;
            aliases.TryAdd(twoLetter, twoLetter);
            string threeLetter = culture.ThreeLetterISOLanguageName.ToLowerInvariant();
            if (threeLetter.Length == 3) aliases.TryAdd(threeLetter, twoLetter);
        }
        return aliases;
    }

    public static string? NormalizeStrongIdentifier(string type, string value)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(value)) return null;
        string canonicalType = type.Trim().ToUpperInvariant() switch
        {
            "ISBN10" or "ISBN13" => "ISBN",
            string result => result,
        };
        string trimmed = value.Trim();
        return canonicalType switch
        {
            "ISBN" => NormalizeIsbn(trimmed) is { } isbn ? $"ISBN:{isbn}" : null,
            "DOI" => NormalizeDoi(trimmed) is { } doi ? $"DOI:{doi}" : null,
            "ASIN" => NormalizeAsin(trimmed) is { } asin ? $"ASIN:{asin}" : null,
            "OCLC" => NormalizeOclc(trimmed) is { } oclc ? $"OCLC:{oclc}" : null,
            _ => null,
        };
    }

    public static string? NormalizeEmbeddedIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        string uuid = trimmed.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)
            ? trimmed[9..]
            : trimmed;
        if (Guid.TryParse(uuid, out Guid parsed)) return "UUID:" + parsed.ToString("D");

        string isbnValue = trimmed.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase)
            ? trimmed[9..]
            : trimmed.StartsWith("isbn:", StringComparison.OrdinalIgnoreCase)
                ? trimmed[5..]
                : trimmed;
        string isbnSyntax = new(isbnValue.Where(character => character is not '-' && !char.IsWhiteSpace(character)).ToArray());
        if (isbnSyntax.All(character => char.IsAsciiDigit(character) || character is 'X' or 'x')
            && NormalizeIsbn(isbnValue) is { } isbn)
            return "ISBN:" + isbn;

        string doiValue = trimmed.StartsWith("urn:doi:", StringComparison.OrdinalIgnoreCase)
            ? trimmed[8..]
            : trimmed;
        if (NormalizeDoi(doiValue) is { } doi) return "DOI:" + doi;
        if (trimmed.StartsWith("asin:", StringComparison.OrdinalIgnoreCase)
            && NormalizeAsin(trimmed[5..]) is { } asin)
            return "ASIN:" + asin;
        if ((trimmed.StartsWith("oclc", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("ocm", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("ocn", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            && NormalizeOclc(trimmed) is { } oclc)
            return "OCLC:" + oclc;
        return null;
    }

    public static string? NormalizeSeries(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string[] tokens = Tokenize(value);
        return tokens.Length == 0 ? null : string.Join(' ', tokens);
    }

    private static string[] Tokenize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        string decomposed;
        try
        {
            decomposed = value.Normalize(NormalizationForm.FormD).ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            return [];
        }
        List<string> tokens = [];
        StringBuilder current = new();
        foreach (Rune rune in decomposed.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
                continue;
            if (Rune.IsLetterOrDigit(rune))
            {
                current.Append(rune.ToString());
                continue;
            }
            Flush(tokens, current);
        }
        Flush(tokens, current);
        return tokens.Distinct(StringComparer.Ordinal).Take(128).ToArray();
    }

    private static void Flush(List<string> tokens, StringBuilder current)
    {
        if (current.Length == 0) return;
        tokens.Add(current.ToString().Normalize(NormalizationForm.FormC));
        current.Clear();
    }

    private static bool IsOrdinalToken(string value)
    {
        if (value.All(char.IsAsciiDigit)) return value.Length <= 4;
        return value.Length <= 8 && value.All(character => character is 'I' or 'V' or 'X' or 'L' or 'C');
    }

    private static string? NormalizeIsbn(string value)
    {
        string isbn = new(value.Where(character => char.IsDigit(character) || character is 'X' or 'x')
            .Select(char.ToUpperInvariant).ToArray());
        if (isbn.Length == 10)
        {
            int sum = 0;
            for (int index = 0; index < isbn.Length; index++)
            {
                if (index < 9 && !char.IsDigit(isbn[index])) return null;
                sum += (10 - index) * (isbn[index] == 'X' ? 10 : isbn[index] - '0');
            }
            return sum % 11 == 0 ? isbn : null;
        }
        if (isbn.Length != 13 || !isbn.All(char.IsAsciiDigit)) return null;
        int weighted = 0;
        for (int index = 0; index < 12; index++)
            weighted += (isbn[index] - '0') * (index % 2 == 0 ? 1 : 3);
        return (10 - weighted % 10) % 10 == isbn[12] - '0' ? isbn : null;
    }

    private static string? NormalizeDoi(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        foreach (string prefix in new[] { "https://doi.org/", "http://doi.org/", "doi:" })
            if (normalized.StartsWith(prefix, StringComparison.Ordinal)) normalized = normalized[prefix.Length..];
        return normalized.StartsWith("10.", StringComparison.Ordinal) && normalized.Contains('/', StringComparison.Ordinal)
            && normalized.Length <= 200 ? normalized : null;
    }

    private static string? NormalizeAsin(string value)
    {
        string normalized = value.Trim().ToUpperInvariant();
        return normalized.Length == 10 && normalized.All(char.IsAsciiLetterOrDigit) ? normalized : null;
    }

    private static string? NormalizeOclc(string value)
    {
        string normalized = value.Trim().ToUpperInvariant();
        foreach (string prefix in new[] { "OCLC", "OCM", "OCN", "ON" })
            if (normalized.StartsWith(prefix, StringComparison.Ordinal)) normalized = normalized[prefix.Length..];
        normalized = new(normalized.Where(char.IsAsciiDigit).ToArray());
        normalized = normalized.TrimStart('0');
        return normalized.Length is > 0 and <= 20 ? normalized : null;
    }
}
