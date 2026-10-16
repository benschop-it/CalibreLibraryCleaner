using System.Diagnostics;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Calibre;

internal sealed class CalibreEbookViewerLauncher(
    CalibreExecutionOptions options,
    Func<ProcessStartInfo, Process?>? startProcess = null)
    : IEbookViewerLauncher
{
    public async Task<EbookViewerLaunchResult> LaunchAsync(
        EbookViewerLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ExecutionPathGuard.TryValidateContainedRegularFile(
                request.LibraryRoot, request.ExpectedRelativePath,
                out string? formatPath, out _))
        {
            return EbookViewerLaunchResult.Failure(
                "EBOOK_FILE_UNAVAILABLE", "The selected book format is missing or cannot be opened safely.");
        }

        bool isPdf = string.Equals(Path.GetExtension(formatPath), ".pdf", StringComparison.OrdinalIgnoreCase);
        if (!isPdf && !string.Equals(Path.GetExtension(formatPath), ".epub", StringComparison.OrdinalIgnoreCase))
        {
            return EbookViewerLaunchResult.Failure(
                "EBOOK_FORMAT_UNSUPPORTED", "Only EPUB and PDF files can be opened.");
        }

        string viewerPath = formatPath!;
        if (!isPdf)
        {
            try
            {
                string trustedCalibredb = Path.GetFullPath(options.TrustedExecutablePath);
                viewerPath = Path.Combine(Path.GetDirectoryName(trustedCalibredb)!, "ebook-viewer.exe");
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return EbookViewerLaunchResult.Failure(
                    "EBOOK_VIEWER_PATH_INVALID", "The configured Calibre viewer path is invalid.");
            }

            if (!File.Exists(viewerPath)
                || !ExecutionPathGuard.TryRejectReparsePoints(viewerPath, true, out _))
            {
                return EbookViewerLaunchResult.Failure(
                    "EBOOK_VIEWER_NOT_FOUND", "Calibre ebook-viewer.exe was not found in the trusted Calibre installation.");
            }
        }

        string? stagingDirectory = null;
        string launchPath = formatPath!;
        if (!isPdf)
        {
            try
            {
                (stagingDirectory, launchPath) = await StageEpubAsync(
                    request.LibraryRoot, formatPath!, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                               or ArgumentException or NotSupportedException or PathTooLongException)
            {
                TryDeleteDirectory(stagingDirectory);
                return EbookViewerLaunchResult.Failure(
                    "EBOOK_VIEWER_STAGING_FAILED", "A safe temporary EPUB viewer copy could not be prepared.");
            }
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = viewerPath,
            UseShellExecute = isPdf,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(viewerPath)!,
        };
        if (!isPdf)
        {
            startInfo.ArgumentList.Add("--new-instance");
            startInfo.ArgumentList.Add(launchPath);
        }
        string viewerName = isPdf ? "The default PDF viewer" : "Calibre ebook-viewer";
        try
        {
            Process? process = (startProcess ?? Process.Start)(startInfo);
            if (process is null && !isPdf)
            {
                TryDeleteDirectory(stagingDirectory);
                return EbookViewerLaunchResult.Failure(
                    "EBOOK_VIEWER_NOT_STARTED", $"{viewerName} could not be started.");
            }
            if (isPdf)
            {
                process?.Dispose();
            }
            else
            {
                RegisterCleanup(process!, stagingDirectory!);
            }
            return EbookViewerLaunchResult.Success();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
            TryDeleteDirectory(stagingDirectory);
            return EbookViewerLaunchResult.Failure(
                "EBOOK_VIEWER_START_FAILED", $"{viewerName} could not be started.");
        }
    }

    private async Task<(string Directory, string Path)> StageEpubAsync(
        string libraryRoot,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (options.ViewerMaximumStagingBytes <= 0
            || options.ViewerMaximumStaleDirectoriesToInspect <= 0
            || options.ViewerStaleAge <= TimeSpan.Zero)
            throw new IOException("Viewer staging options are invalid.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ViewerStagingRoot));
        string library = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        if (string.Equals(root, library, StringComparison.OrdinalIgnoreCase)
            || ExecutionPathGuard.IsContained(library, root)
            || ExecutionPathGuard.IsContained(root, library))
            throw new IOException("Viewer staging must be outside the library.");
        Directory.CreateDirectory(root);
        if (!ExecutionPathGuard.TryRejectReparsePoints(root, true, out _))
            throw new IOException("Viewer staging must be physical.");
        PruneStaleDirectories(root);

        FileInfo source = new(sourcePath);
        if (source.Length <= 0 || source.Length > options.ViewerMaximumStagingBytes)
            throw new IOException("The EPUB exceeds viewer staging bounds.");
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "book.epub");
        try
        {
            await using FileStream input = new(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream output = new(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, 64 * 1024, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return (directory, destination);
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    private void PruneStaleDirectories(string root)
    {
        DateTime cutoff = DateTime.UtcNow - options.ViewerStaleAge;
        try
        {
            foreach (DirectoryInfo directory in new DirectoryInfo(root).EnumerateDirectories()
                         .OrderBy(value => value.CreationTimeUtc)
                         .Take(options.ViewerMaximumStaleDirectoriesToInspect))
            {
                if (directory.CreationTimeUtc >= cutoff) continue;
                TryDeleteDirectory(directory.FullName);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void RegisterCleanup(Process process, string directory)
    {
        int cleaned = 0;
        void Cleanup()
        {
            if (Interlocked.Exchange(ref cleaned, 1) != 0) return;
            process.Dispose();
            TryDeleteDirectory(directory);
        }

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Cleanup();
            if (process.HasExited) Cleanup();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
            process.Dispose();
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (path is null) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
