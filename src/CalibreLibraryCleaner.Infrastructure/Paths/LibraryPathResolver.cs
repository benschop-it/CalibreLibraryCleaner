using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;

namespace CalibreLibraryCleaner.Infrastructure.Paths;

internal sealed class LibraryPathResolver : ILibraryPathResolver
{
    private static readonly char[] DirectorySeparators = ['/', '\\'];
    private static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();
    private static readonly HashSet<string> WindowsReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public Task<LibraryValidationOutcome> ValidateAsync(
        string? candidatePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return Task.FromResult(Failure(
                LibraryErrorCode.EmptyPath,
                "Choose a Calibre library folder.",
                "Select the top-level folder that directly contains metadata.db."));
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(Failure(
                LibraryErrorCode.FolderNotFound,
                "The selected folder path is invalid.",
                "Choose an existing Calibre library folder."));
        }

        if (!Directory.Exists(root))
        {
            return Task.FromResult(Failure(
                LibraryErrorCode.FolderNotFound,
                "The selected library folder does not exist.",
                "Choose an existing Calibre library folder."));
        }

        try
        {
            FileAttributes rootAttributes = File.GetAttributes(root);
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                return Task.FromResult(Failure(
                    LibraryErrorCode.FolderNotReadable,
                    "Linked library folders are not supported safely.",
                    "Choose the physical Calibre library folder rather than a symbolic link or junction."));
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return Task.FromResult(Failure(
                LibraryErrorCode.FolderNotReadable,
                "The selected library folder cannot be read.",
                "Check folder permissions and try again."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string databasePath = Path.Combine(root, "metadata.db");
        if (!File.Exists(databasePath))
        {
            if (Directory.Exists(databasePath))
            {
                return Task.FromResult(Failure(
                    LibraryErrorCode.MetadataDatabaseNotAFile,
                    "metadata.db is not a file.",
                    "Choose a valid Calibre library whose top-level metadata.db is a database file."));
            }

            return Task.FromResult(Failure(
                LibraryErrorCode.MetadataDatabaseNotFound,
                "The selected folder does not contain metadata.db.",
                "Choose the top-level Calibre library folder that directly contains metadata.db."));
        }

        try
        {
            FileAttributes databaseAttributes = File.GetAttributes(databasePath);
            if ((databaseAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return Task.FromResult(Failure(
                    LibraryErrorCode.MetadataDatabaseNotAFile,
                    "metadata.db is not a regular file.",
                    "Choose a valid Calibre library with a regular metadata.db file."));
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return Task.FromResult(Failure(
                LibraryErrorCode.MetadataDatabaseNotReadable,
                "metadata.db cannot be read.",
                "Check file permissions, close tools maintaining the library, and try again."));
        }

        return Task.FromResult(LibraryValidationOutcome.Success(new(root, databasePath)));
    }

    public ResolvedFormatPathOutcome ResolveFormat(
        ValidatedLibraryLocation library,
        string relativeDirectory,
        string storedName,
        string format)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(relativeDirectory);
        ArgumentNullException.ThrowIfNull(storedName);
        ArgumentNullException.ThrowIfNull(format);

        if (!TrySplitRelativePath(relativeDirectory, out string[] directoryParts, out string? reason))
        {
            return ResolvedFormatPathOutcome.Failure(reason!);
        }

        if (!IsSafeFileStem(storedName))
        {
            return ResolvedFormatPathOutcome.Failure("The stored format name is not a safe filename stem.");
        }

        if (!IsSafeFormat(format))
        {
            return ResolvedFormatPathOutcome.Failure("The stored format is not a safe extension token.");
        }

        string expectedFileName = $"{storedName}.{format.ToLowerInvariant()}";
        string relativePath = Path.Combine([.. directoryParts, expectedFileName]);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(library.LibraryRoot, relativePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ResolvedFormatPathOutcome.Failure("The expected format path cannot be canonicalized.");
        }

        if (!IsContained(library.LibraryRoot, fullPath))
        {
            return ResolvedFormatPathOutcome.Failure("The expected format path leaves the selected library.");
        }

        string current = library.LibraryRoot;
        foreach (string part in directoryParts)
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current))
            {
                break;
            }

            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return ResolvedFormatPathOutcome.Failure(
                        "The expected format path passes through a symbolic link or junction.");
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                return ResolvedFormatPathOutcome.Failure("The expected format path cannot be inspected safely.");
            }
        }

        return ResolvedFormatPathOutcome.Success(new(fullPath, relativePath));
    }

    public ValueTask<bool> FileExistsAsync(
        ResolvedFormatPath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(File.Exists(path.FullPath));
    }

    private static bool TrySplitRelativePath(
        string relativeDirectory,
        out string[] parts,
        out string? reason)
    {
        parts = [];
        reason = null;
        if (string.IsNullOrWhiteSpace(relativeDirectory) || Path.IsPathRooted(relativeDirectory))
        {
            reason = "The Calibre-managed book directory is empty or rooted.";
            return false;
        }

        parts = relativeDirectory.Split(DirectorySeparators, StringSplitOptions.None);
        if (parts.Any(part =>
                string.IsNullOrWhiteSpace(part)
                || part is "." or ".."
                || part.EndsWith(' ')
                || part.EndsWith('.')
                || IsWindowsReservedName(part)
                || part.IndexOfAny(InvalidFileNameCharacters) >= 0))
        {
            reason = "The Calibre-managed book directory contains an invalid path component.";
            return false;
        }

        return true;
    }

    private static bool IsSafeFileStem(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && !value.EndsWith(' ')
        && !value.EndsWith('.')
        && !IsWindowsReservedName(value)
        && value.IndexOfAny(DirectorySeparators) < 0
        && value.IndexOfAny(InvalidFileNameCharacters) < 0;

    private static bool IsSafeFormat(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.All(character => char.IsAsciiLetterOrDigit(character));

    private static bool IsContained(string root, string path)
    {
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsReservedName(string value)
    {
        int dotIndex = value.IndexOf('.');
        string stem = dotIndex < 0 ? value : value[..dotIndex];
        return WindowsReservedNames.Contains(stem);
    }

    private static LibraryValidationOutcome Failure(
        LibraryErrorCode code,
        string message,
        string suggestedAction) =>
        LibraryValidationOutcome.Failure(new(code, message, suggestedAction));
}
