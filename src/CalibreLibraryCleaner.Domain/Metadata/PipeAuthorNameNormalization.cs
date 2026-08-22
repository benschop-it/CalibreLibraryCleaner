using System.Text;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Metadata;

public sealed record PipeAuthorNameNormalization(string DisplayName, string SortName);

public static class PipeAuthorNameNormalizationPolicy
{
    public static bool TryNormalizeBook(
        IReadOnlyList<BookAuthor> authors,
        out IReadOnlyList<string> displayNames,
        out IReadOnlyList<string> sortNames)
    {
        ArgumentNullException.ThrowIfNull(authors);
        List<string> displays = [];
        List<string> sorts = [];
        foreach (BookAuthor author in authors)
        {
            PipeAuthorNameNormalization? value;
            bool normalized = string.Equals(author.Name, author.SortName, StringComparison.Ordinal)
                ? TryNormalizeSortForm(author.Name, out value)
                : TryNormalize(author.Name, out value)
                    && TryNormalizeSortForm(
                        author.SortName, out PipeAuthorNameNormalization? normalizedSort)
                    && value == normalizedSort;
            if (!normalized)
            {
                displayNames = [];
                sortNames = [];
                return false;
            }
            displays.Add(value!.DisplayName);
            sorts.Add(value.SortName);
        }
        if (displays.Count == 0 || displays.Distinct(StringComparer.Ordinal).Count() != displays.Count)
        {
            displayNames = [];
            sortNames = [];
            return false;
        }
        displayNames = displays;
        sortNames = sorts;
        return true;
    }

    public static bool TryNormalizeSortForm(
        string? value,
        out PipeAuthorNameNormalization? normalization)
    {
        normalization = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        int separator = value.IndexOf(',', StringComparison.Ordinal);
        if (separator <= 0 || separator != value.LastIndexOf(',') || separator == value.Length - 1)
            return false;
        return Create(value[..separator], value[(separator + 1)..], out normalization);
    }

    public static bool TryNormalize(
        string? value,
        out PipeAuthorNameNormalization? normalization)
    {
        normalization = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        int separator = value.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0 || separator != value.LastIndexOf('|') || separator == value.Length - 1)
            return false;
        return Create(value[..separator], value[(separator + 1)..], out normalization);
    }

    private static bool Create(
        string familyValue,
        string givenValue,
        out PipeAuthorNameNormalization? normalization)
    {
        normalization = null;
        string family = Normalize(familyValue);
        string given = Normalize(givenValue);
        if (family.Length == 0 || given.Length == 0) return false;
        string display = $"{given} {family}";
        string sort = $"{family}, {given}";
        if (display.Length > 256 || sort.Length > 256) return false;
        normalization = new(display, sort);
        return true;
    }

    private static string Normalize(string value) => string.Join(' ',
        value.Normalize(NormalizationForm.FormC).Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
