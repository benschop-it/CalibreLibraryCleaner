namespace CalibreLibraryCleaner.Domain.Metadata;

public static class EditionMetadataAuthorWritePolicy
{
    public static bool TryPrepare(
        IEnumerable<string> authors,
        out IReadOnlyList<string> prepared)
    {
        ArgumentNullException.ThrowIfNull(authors);
        List<string> result = [];
        foreach (string author in authors)
        {
            string value = Normalize(author);
            if (value.Length == 0)
            {
                prepared = [];
                return false;
            }
            if (!value.Contains(" & ", StringComparison.Ordinal))
            {
                result.Add(value);
                continue;
            }
            string[] parts = value.Split(" & ", StringSplitOptions.None);
            if (parts.Length is < 2 or > 8 || parts.Any(part => !TryCommaName(part, out _)))
            {
                prepared = [];
                return false;
            }
            result.AddRange(parts.Select(part =>
            {
                TryCommaName(part, out string? display);
                return display!;
            }));
        }
        string[] distinct = result.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length is 0 or > 16 || distinct.Any(value => value.Length > 256))
        {
            prepared = [];
            return false;
        }
        prepared = distinct;
        return true;
    }

    private static bool TryCommaName(string value, out string? display)
    {
        display = null;
        int comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma <= 0 || comma != value.LastIndexOf(',') || comma == value.Length - 1)
            return false;
        string family = Normalize(value[..comma]);
        string given = Normalize(value[(comma + 1)..]);
        if (family.Length == 0 || given.Length == 0) return false;
        display = $"{given} {family}";
        return true;
    }

    private static string Normalize(string value) => string.Join(' ',
        value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
