using System.Security;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Infrastructure.Hashing;

internal sealed class PhysicalFormatFileProbe : IFormatFileProbe
{
    public async Task<FormatFileProbeResult> ProbeAsync(
        ResolvedFormatPath path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            EnsureSafeManagedPath(path);
            FileInfo before = new(path.FullPath);
            before.Refresh();
            if (!before.Exists)
                return FormatFileProbeResult.Failure(FormatFileProbeStatus.Missing, "FileMissing");
            if ((before.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return FormatFileProbeResult.Failure(FormatFileProbeStatus.UnsafePath, "UnsafeLeaf");
            await using (FileStream stream = new(
                             path.FullPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             1,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[1];
                _ = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            FileInfo after = new(path.FullPath);
            after.Refresh();
            FormatFileObservation beforeObservation = Observation(before);
            FormatFileObservation afterObservation = Observation(after);
            if (beforeObservation != afterObservation)
                return FormatFileProbeResult.Failure(FormatFileProbeStatus.Inaccessible, "ObservationChanged");
            return FormatFileProbeResult.Success(afterObservation);
        }
        catch (FileNotFoundException)
        {
            return FormatFileProbeResult.Failure(FormatFileProbeStatus.Missing, "FileMissing");
        }
        catch (DirectoryNotFoundException)
        {
            return FormatFileProbeResult.Failure(FormatFileProbeStatus.Missing, "DirectoryMissing");
        }
        catch (UnsafeManagedPathException)
        {
            return FormatFileProbeResult.Failure(FormatFileProbeStatus.UnsafePath, "UnsafeManagedPath");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return FormatFileProbeResult.Failure(FormatFileProbeStatus.Inaccessible, exception.GetType().Name);
        }
    }

    private static void EnsureSafeManagedPath(ResolvedFormatPath path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.LibraryRoot));
        string fullPath = Path.GetFullPath(path.FullPath);
        string expectedPath = Path.GetFullPath(Path.Combine(root, path.RelativePath));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(fullPath, expectedPath, comparison)
            || !fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new UnsafeManagedPathException();
        FileAttributes rootAttributes = File.GetAttributes(root);
        if ((rootAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
            throw new UnsafeManagedPathException();
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent is null) throw new UnsafeManagedPathException();
        string current = root;
        foreach (string part in Path.GetRelativePath(root, parent).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new UnsafeManagedPathException();
        }
    }

    private static FormatFileObservation Observation(FileInfo info) => new(
        info.Length,
        info.CreationTimeUtc,
        info.LastWriteTimeUtc,
        (int)info.Attributes);

    private sealed class UnsafeManagedPathException : Exception;
}
