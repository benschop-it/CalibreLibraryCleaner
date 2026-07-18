using CalibreLibraryCleaner.Application.Recommendations;

namespace CalibreLibraryCleaner.Infrastructure.Recommendations;

internal static class RecommendationExportPathGuard
{
    public static RecommendationExportPathOutcome Validate(string libraryRoot, string destinationPath)
    {
        try
        {
            string canonicalLibrary = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string canonicalDestination = Path.GetFullPath(destinationPath);
            string? directory = Path.GetDirectoryName(canonicalDestination);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) || !Directory.Exists(canonicalLibrary))
            {
                return RecommendationExportPathOutcome.Failure("DESTINATION_DIRECTORY_MISSING", "Choose an existing directory outside the Calibre library.");
            }

            if (File.Exists(canonicalDestination)
                && (File.GetAttributes(canonicalDestination) & FileAttributes.ReparsePoint) != 0)
            {
                return RecommendationExportPathOutcome.Failure("DESTINATION_REPARSE_POINT", "The selected destination file is a reparse point and is not safe for export.");
            }

            DirectoryInfo? current = new(directory);
            while (current is not null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return RecommendationExportPathOutcome.Failure("DESTINATION_REPARSE_POINT", "The selected destination traverses a reparse point and is not safe for export.");
                }

                current = current.Parent;
            }

            string physicalLibrary = ResolveExistingDirectory(canonicalLibrary);
            string physicalDirectory = ResolveExistingDirectory(directory);
            string physicalDestination = Path.Combine(physicalDirectory, Path.GetFileName(canonicalDestination));
            string libraryPrefix = physicalLibrary.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (string.Equals(physicalDestination, physicalLibrary, StringComparison.OrdinalIgnoreCase)
                || physicalDestination.StartsWith(libraryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return RecommendationExportPathOutcome.Failure("DESTINATION_INSIDE_LIBRARY", "Recommendation review artifacts cannot be written inside the Calibre library.");
            }

            return RecommendationExportPathOutcome.Success(canonicalDestination);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return RecommendationExportPathOutcome.Failure("DESTINATION_INVALID", "The selected export destination could not be validated safely.");
        }
    }

    private static string ResolveExistingDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new IOException("The directory root could not be resolved.");
        string resolved = root;
        string remainder = fullPath[root.Length..];
        foreach (string segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            DirectoryInfo directory = new(Path.Combine(resolved, segment));
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo target = directory.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new IOException("The directory reparse target could not be resolved.");
                resolved = Path.GetFullPath(target.FullName);
            }
            else
            {
                resolved = directory.FullName;
            }
        }

        return resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

internal sealed record RecommendationExportPathOutcome(string? CanonicalDestination, RecommendationExportError? Error)
{
    public bool IsSuccess => CanonicalDestination is not null;
    public static RecommendationExportPathOutcome Success(string path) => new(path, null);
    public static RecommendationExportPathOutcome Failure(string code, string message) => new(null, new(code, message));
}
