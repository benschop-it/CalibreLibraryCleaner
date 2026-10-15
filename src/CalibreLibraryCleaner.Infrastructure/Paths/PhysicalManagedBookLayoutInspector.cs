using System.Globalization;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Paths;

internal sealed class PhysicalManagedBookLayoutInspector : IManagedBookLayoutInspector
{
    private const int MaximumFilesPerBookDirectory = 1_024;
    private const int MaximumManagedDirectories = 100_000;

    public Task<IReadOnlyList<ManagedBookLayoutIssue>> InspectAsync(
        ValidatedLibraryLocation location,
        IReadOnlyList<ManagedBookLayoutInspectionTarget> targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(targets);
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.LibraryRoot));
        if (!Directory.Exists(root)
            || !ExecutionPathGuard.TryRejectReparsePoints(root, true, out _))
            throw new IOException("The library root cannot be inspected safely.");

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        HashSet<string> formatExtensions = targets
            .SelectMany(value => value.RegisteredFormatRelativePaths)
            .Select(Path.GetExtension)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(pathComparer);
        Dictionary<CalibreBookId, string> expectedDirectories = [];
        HashSet<ManagedBookLayoutIssue> issues = [];

        foreach (ManagedBookLayoutInspectionTarget target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryContainedPath(root, target.RelativeDirectory, out string? directory)
                || !Directory.Exists(directory)
                || !ExecutionPathGuard.TryRejectReparsePoints(directory, true, out _))
            {
                issues.Add(new(target.BookId, ManagedBookLayoutProblem.InspectionUnavailable));
                continue;
            }
            expectedDirectories[target.BookId] = directory;
            HashSet<string> registered = target.RegisteredFormatRelativePaths
                .Select(value => TryContainedPath(root, value, out string? path) ? path : null)
                .Where(value => value is not null)
                .Select(value => value!)
                .ToHashSet(pathComparer);
            try
            {
                int fileCount = 0;
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++fileCount > MaximumFilesPerBookDirectory)
                    {
                        issues.Add(new(target.BookId, ManagedBookLayoutProblem.InspectionUnavailable));
                        break;
                    }
                    string canonical = Path.GetFullPath(file);
                    if (registered.Contains(canonical)) continue;
                    if (formatExtensions.Contains(Path.GetExtension(canonical)))
                    {
                        issues.Add(new(target.BookId, ManagedBookLayoutProblem.UnregisteredFormatFile));
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                issues.Add(new(target.BookId, ManagedBookLayoutProblem.InspectionUnavailable));
            }
        }

        InspectDuplicateRecordDirectories(
            root, expectedDirectories, issues, pathComparer, cancellationToken);
        return Task.FromResult<IReadOnlyList<ManagedBookLayoutIssue>>(issues
            .OrderBy(value => value.BookId.Value)
            .ThenBy(value => value.Problem)
            .ToArray());
    }

    private static void InspectDuplicateRecordDirectories(
        string root,
        IReadOnlyDictionary<CalibreBookId, string> expectedDirectories,
        HashSet<ManagedBookLayoutIssue> issues,
        StringComparer pathComparer,
        CancellationToken cancellationToken)
    {
        int directoryCount = 0;
        try
        {
            foreach (string authorDirectory in Directory.EnumerateDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(authorDirectory);
                if (name.Length > 0 && name[0] == '.'
                    || !ExecutionPathGuard.TryRejectReparsePointLeaf(authorDirectory, true, out _))
                    continue;
                foreach (string bookDirectory in Directory.EnumerateDirectories(authorDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++directoryCount > MaximumManagedDirectories)
                    {
                        foreach (CalibreBookId targetBookId in expectedDirectories.Keys)
                            issues.Add(new(targetBookId, ManagedBookLayoutProblem.InspectionUnavailable));
                        return;
                    }
                    if (!TryRecordId(Path.GetFileName(bookDirectory), out CalibreBookId bookId)
                        || !expectedDirectories.TryGetValue(bookId, out string? expected)) continue;
                    string canonical = Path.GetFullPath(bookDirectory);
                    if (!pathComparer.Equals(canonical, expected))
                        issues.Add(new(bookId, ManagedBookLayoutProblem.DuplicateRecordDirectory));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            foreach (CalibreBookId targetBookId in expectedDirectories.Keys)
                issues.Add(new(targetBookId, ManagedBookLayoutProblem.InspectionUnavailable));
        }
    }

    private static bool TryContainedPath(string root, string relativePath, out string? fullPath)
    {
        fullPath = null;
        try
        {
            string candidate = Path.GetFullPath(Path.Combine(
                root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!ExecutionPathGuard.IsContained(root, candidate)) return false;
            fullPath = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryRecordId(string directoryName, out CalibreBookId bookId)
    {
        bookId = default;
        int open = directoryName.LastIndexOf(" (", StringComparison.Ordinal);
        if (open < 0 || !directoryName.EndsWith(')')) return false;
        ReadOnlySpan<char> value = directoryName.AsSpan(open + 2, directoryName.Length - open - 3);
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id)
            || id <= 0) return false;
        bookId = new(id);
        return true;
    }
}
