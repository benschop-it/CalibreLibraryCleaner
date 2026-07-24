namespace CalibreLibraryCleaner.Infrastructure.Plans;

internal static class CleanupPlanPathGuard
{
    private const string Extension = ".cleanup-plan.json";

    public static PathGuardResult ValidateExport(string libraryRoot, string destinationPath) =>
        Validate(libraryRoot, destinationPath, requireExistingFile: false);

    public static PathGuardResult ValidateImport(string libraryRoot, string sourcePath) =>
        Validate(libraryRoot, sourcePath, requireExistingFile: true);

    private static PathGuardResult Validate(string libraryRoot, string artifactPath, bool requireExistingFile)
    {
        try
        {
            string canonicalLibrary = Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string canonicalArtifact = Path.GetFullPath(artifactPath);
            string? directory = Path.GetDirectoryName(canonicalArtifact);
            if (!Directory.Exists(canonicalLibrary) || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return PathGuardResult.Failure("CLEANUP_PLAN_DIRECTORY_MISSING", "Choose an existing directory outside the Calibre library.");
            if (!canonicalArtifact.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                return PathGuardResult.Failure("CLEANUP_PLAN_EXTENSION_REQUIRED", "Cleanup plan artifacts must use the .cleanup-plan.json extension.");
            if (requireExistingFile && (!File.Exists(canonicalArtifact)
                || (File.GetAttributes(canonicalArtifact) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
                return PathGuardResult.Failure("CLEANUP_PLAN_SOURCE_INVALID", "Choose an existing regular cleanup-plan file.");
            if (!requireExistingFile && File.Exists(canonicalArtifact)
                && (File.GetAttributes(canonicalArtifact) & FileAttributes.ReparsePoint) != 0)
                return PathGuardResult.Failure("CLEANUP_PLAN_REPARSE_POINT", "The cleanup-plan destination cannot be a reparse point.");
            if (ContainsReparsePoint(directory))
                return PathGuardResult.Failure("CLEANUP_PLAN_REPARSE_POINT", "The cleanup-plan path traverses a reparse point and cannot be proven safe.");

            string physicalLibrary = ResolveExistingDirectory(canonicalLibrary);
            string physicalDirectory = ResolveExistingDirectory(directory);
            string physicalArtifact = Path.Combine(physicalDirectory, Path.GetFileName(canonicalArtifact));
            string prefix = physicalLibrary + Path.DirectorySeparatorChar;
            if (string.Equals(physicalArtifact, physicalLibrary, StringComparison.OrdinalIgnoreCase)
                || physicalArtifact.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return PathGuardResult.Failure("CLEANUP_PLAN_INSIDE_LIBRARY", "Cleanup-plan files must remain outside the selected Calibre library.");
            return PathGuardResult.Success(canonicalArtifact);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return PathGuardResult.Failure("CLEANUP_PLAN_PATH_INVALID", "The cleanup-plan path could not be validated safely.");
        }
    }

    private static bool ContainsReparsePoint(string path)
    {
        DirectoryInfo? current = new(path);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            current = current.Parent;
        }
        return false;
    }

    private static string ResolveExistingDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? throw new IOException("The path root is unavailable.");
        string resolved = root;
        foreach (string segment in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            DirectoryInfo directory = new(Path.Combine(resolved, segment));
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                FileSystemInfo target = directory.ResolveLinkTarget(true) ?? throw new IOException("A reparse target could not be resolved.");
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

internal sealed record PathGuardResult(string? CanonicalPath, string? ErrorCode, string? ErrorMessage)
{
    public bool IsSuccess => CanonicalPath is not null;
    public static PathGuardResult Success(string path) => new(path, null, null);
    public static PathGuardResult Failure(string code, string message) => new(null, code, message);
}
