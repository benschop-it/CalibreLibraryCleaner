using System.Diagnostics;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Calibre;

internal sealed class CalibreEbookViewerLauncher(CalibreExecutionOptions options)
    : IEbookViewerLauncher
{
    public Task<EbookViewerLaunchResult> LaunchAsync(
        EbookViewerLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string viewerPath;
        try
        {
            string trustedCalibredb = Path.GetFullPath(options.TrustedExecutablePath);
            viewerPath = Path.Combine(Path.GetDirectoryName(trustedCalibredb)!, "ebook-viewer.exe");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(EbookViewerLaunchResult.Failure(
                "EBOOK_VIEWER_PATH_INVALID", "The configured Calibre viewer path is invalid."));
        }

        if (!File.Exists(viewerPath)
            || !ExecutionPathGuard.TryRejectReparsePoints(viewerPath, true, out _))
        {
            return Task.FromResult(EbookViewerLaunchResult.Failure(
                "EBOOK_VIEWER_NOT_FOUND", "Calibre ebook-viewer.exe was not found in the trusted Calibre installation."));
        }

        if (!ExecutionPathGuard.TryValidateContainedRegularFile(
                request.LibraryRoot, request.ExpectedRelativePath,
                out string? formatPath, out _))
        {
            return Task.FromResult(EbookViewerLaunchResult.Failure(
                "EBOOK_FILE_UNAVAILABLE", "The selected book format is missing or cannot be opened safely."));
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = viewerPath,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(viewerPath)!,
        };
        startInfo.ArgumentList.Add(formatPath!);
        try
        {
            Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return Task.FromResult(EbookViewerLaunchResult.Failure(
                    "EBOOK_VIEWER_NOT_STARTED", "Calibre ebook-viewer could not be started."));
            }
            process.Dispose();
            return Task.FromResult(EbookViewerLaunchResult.Success());
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                           or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(EbookViewerLaunchResult.Failure(
                "EBOOK_VIEWER_START_FAILED", "Calibre ebook-viewer could not be started."));
        }
    }
}
