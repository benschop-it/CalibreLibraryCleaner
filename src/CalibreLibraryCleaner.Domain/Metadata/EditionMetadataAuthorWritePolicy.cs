using System.Text;
using System.Text.RegularExpressions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Metadata;

public static partial class EditionMetadataAuthorWritePolicy
{
    public const string PseudonymAnnotationWithheld = "METADATA.AUTHORS.PSEUDONYM_ANNOTATION_WITHHELD";
    public const string LegacyAuthorOrderUnavailable = "METADATA.AUTHORS.LEGACY_ORDER_UNAVAILABLE";

    public static bool TryPrepare(
        CalibreBook current,
        EditionMetadataCandidate candidate,
        out IReadOnlyList<string> prepared)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);
        if (!TryPrepare(candidate.Authors, out prepared)) return false;
        if (candidate.AuthorOrderKnown || prepared.Count <= 1) return true;
        if (!TryPrepare(current.Authors.Select(value => value.Name), out IReadOnlyList<string> currentCredits)
            || currentCredits.Count != prepared.Count
            || currentCredits.Distinct(StringComparer.OrdinalIgnoreCase).Count() != currentCredits.Count)
        {
            prepared = [];
            return false;
        }
        Dictionary<string, string> corresponding = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in prepared)
        {
            if (corresponding.TryAdd(name, name)) continue;
            prepared = [];
            return false;
        }
        if (currentCredits.Any(name => !corresponding.ContainsKey(name)))
        {
            prepared = [];
            return false;
        }
        prepared = currentCredits.Select(name => corresponding[name]).ToArray();
        return true;
    }

    public static bool HasPseudonymAnnotations(IEnumerable<string> authors)
    {
        ArgumentNullException.ThrowIfNull(authors);
        return authors.Any(HasPseudonymAnnotation);
    }

    public static bool TryPrepare(
        IEnumerable<string> authors,
        out IReadOnlyList<string> prepared)
    {
        ArgumentNullException.ThrowIfNull(authors);
        List<string> result = [];
        foreach (string author in authors)
        {
            if (HasPseudonymAnnotation(author))
            {
                prepared = [];
                return false;
            }
            string value = Normalize(author);
            if (value.Length == 0)
            {
                prepared = [];
                return false;
            }
            string[] credits = value.Split(" & ", StringSplitOptions.None);
            if (credits.Length > 8)
            {
                prepared = [];
                return false;
            }
            foreach (string credit in credits)
            {
                if (credits.Length == 1 && credit.IndexOfAny([',', '|']) < 0)
                {
                    result.Add(credit);
                    continue;
                }
                if (!TryExplicitNames(credit, out IReadOnlyList<string> displays))
                {
                    prepared = [];
                    return false;
                }
                result.AddRange(displays);
            }
        }
        string[] distinct = result.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length is 0 or > 16 || distinct.Any(value => value.Length > 256))
        {
            prepared = [];
            return false;
        }
        string[] harmonized = distinct.Select(AuthorDisplayNameHarmonizationPolicy.NormalizeDisplay).ToArray();
        if (harmonized.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256)
            || harmonized.Distinct(StringComparer.Ordinal).Count() != distinct.Length)
        {
            prepared = [];
            return false;
        }
        prepared = harmonized;
        return true;
    }

    private static bool TryExplicitNames(string value, out IReadOnlyList<string> displays)
    {
        displays = [];
        bool pipe = value.Contains('|', StringComparison.Ordinal);
        if (value.Length > 512 || value.IndexOfAny(['(', ')', '[', ']', '{', '}', ';']) >= 0
            || pipe && value.Contains(',', StringComparison.Ordinal))
            return false;
        string[] parts = value.Split(pipe ? '|' : ',', StringSplitOptions.None);
        if (parts.Length is < 2 or > 16 || parts.Length % 2 != 0
            || !pipe && parts.Length != 2
            || parts.Any(part => string.IsNullOrWhiteSpace(part) || !part.EnumerateRunes().Any(Rune.IsLetter)))
            return false;
        List<string> result = [];
        for (int index = 0; index < parts.Length; index += 2)
        {
            if (!PipeAuthorNameNormalizationPolicy.TryNormalizeSortForm(
                    $"{parts[index]}, {parts[index + 1]}", out PipeAuthorNameNormalization? normalization))
                return false;
            result.Add(normalization!.DisplayName);
        }
        displays = result;
        return true;
    }

    private static string Normalize(string value) => string.Join(' ',
        value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool HasPseudonymAnnotation(string value) =>
        AuthorPseudonymAnnotationPolicy.ContainsMarker(value)
        || value.Length is > 0 and <= 512 && MisspelledPseudonymMarker().IsMatch(value);

    [GeneratedRegex(@"(?:\A|[\s(\[])(?:is\s+)?psued\.?(?:\s|[)\]]|\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex MisspelledPseudonymMarker();
}
