namespace CalibreLibraryCleaner.Infrastructure.Epub;

internal static class EpubArchivePathResolver
{
    public static bool TryNormalizeEntryName(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Split('/').Any(part => part == ".."))
        {
            return false;
        }

        return TryNormalize(value, out normalized);
    }

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || value.Contains('\\') || value.StartsWith('/') || value.Contains(':'))
        {
            return false;
        }

        List<string> parts = [];
        foreach (string part in value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return false;
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(part);
            }
        }

        normalized = string.Join('/', parts);
        return normalized.Length > 0;
    }

    public static bool TryResolve(string baseFile, string reference, out string normalized)
    {
        normalized = string.Empty;
        string value = reference.Split('#', 2)[0].Split('?', 2)[0];
        if (string.IsNullOrEmpty(value)
            || value.StartsWith('/')
            || value.StartsWith('\\')
            || value.Contains('\\')
            || value.Contains('\0')
            || value.Contains(':')) return false;
        string? directory = baseFile.Contains('/') ? baseFile[..baseFile.LastIndexOf('/')] : null;
        return TryNormalize(directory is null ? value : $"{directory}/{value}", out normalized);
    }

    public static bool IsRemote(string value) => value.StartsWith("//", StringComparison.Ordinal)
        || Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme.Length > 0;
}
