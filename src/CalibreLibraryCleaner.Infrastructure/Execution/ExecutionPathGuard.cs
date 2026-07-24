namespace CalibreLibraryCleaner.Infrastructure.Execution;

internal static class ExecutionPathGuard
{
    public static bool TryValidateExternalDirectory(
        string libraryRoot,
        string candidate,
        bool mustExist,
        out string? canonical,
        out string? reason)
    {
        canonical = null;
        reason = null;
        if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(candidate))
        {
            reason = "The library and external destination are required.";
            return false;
        }

        string library;
        string destination;
        try
        {
            library = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "The destination path cannot be canonicalized.";
            return false;
        }

        if (mustExist && !Directory.Exists(destination))
        {
            reason = "The external destination directory does not exist.";
            return false;
        }

        if (File.Exists(destination))
        {
            reason = "The external destination is not a directory.";
            return false;
        }

        if (IsSameOrContained(library, destination) || IsSameOrContained(destination, library))
        {
            reason = "The backup and journal destination must be physically separate from the Calibre library.";
            return false;
        }

        if (!TryRejectReparsePoints(destination, mustExist, out reason)) return false;
        canonical = destination;
        return true;
    }

    public static bool TryValidateContainedRegularFile(
        string root,
        string relativePath,
        out string? fullPath,
        out string? reason)
    {
        fullPath = null;
        reason = null;
        string canonicalRoot;
        string candidate;
        try
        {
            canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            candidate = Path.GetFullPath(Path.Combine(canonicalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "The managed source path cannot be canonicalized.";
            return false;
        }

        if (!IsContained(canonicalRoot, candidate) || !File.Exists(candidate))
        {
            reason = "The managed source file is missing or leaves the selected library.";
            return false;
        }

        if (!TryRejectReparsePoints(candidate, true, out reason)) return false;
        try
        {
            FileAttributes attributes = File.GetAttributes(candidate);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                reason = "The managed source is not a regular file.";
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reason = "The managed source cannot be inspected safely.";
            return false;
        }

        fullPath = candidate;
        return true;
    }

    public static bool IsContained(string root, string path)
    {
        string canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string canonicalPath = Path.GetFullPath(path);
        string prefix = canonicalRoot + Path.DirectorySeparatorChar;
        return canonicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryRejectReparsePoints(string path, bool leafExists, out string? reason)
    {
        reason = null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "The path cannot be canonicalized.";
            return false;
        }

        string? current = leafExists ? full : Path.GetDirectoryName(full);
        while (!string.IsNullOrWhiteSpace(current) && (Directory.Exists(current) || File.Exists(current)))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "Symbolic links and junctions are not accepted at execution boundaries.";
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                reason = "The path cannot be inspected safely.";
                return false;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            current = parent?.FullName;
        }

        return true;
    }

    private static bool IsSameOrContained(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) || IsContained(root, path);
}
