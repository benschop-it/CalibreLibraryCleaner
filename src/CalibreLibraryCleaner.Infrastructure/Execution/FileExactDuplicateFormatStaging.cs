using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Calibre;

namespace CalibreLibraryCleaner.Infrastructure.Execution;

internal sealed class FileExactDuplicateFormatStaging(ExecutionStorageOptions options) : IExactDuplicateFormatStaging
{
    public async Task<StagedExactDuplicateFormat> StageAsync(
        CleanupExecutionId executionId,
        string libraryRoot,
        CalibreBookId recordId,
        BookFormat format,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.FileStatus != FormatFileStatus.Present || format.Fingerprint is null
            || format.Observation is null || string.IsNullOrWhiteSpace(format.ExpectedRelativePath))
            throw new InvalidOperationException("Only a scan-observed present format can be staged for transfer.");
        if (!ExecutionPathGuard.TryValidateContainedRegularFile(libraryRoot,
                format.ExpectedRelativePath, out string? source, out string? reason))
            throw new IOException(reason ?? "The managed source format cannot be staged.");

        string root = Path.GetFullPath(options.TransferStagingRoot);
        if (!ExecutionPathGuard.TryValidateExternalDirectory(libraryRoot, root, false,
                out string? canonicalRoot, out reason))
            throw new IOException(reason ?? "The transfer staging root is invalid.");
        Directory.CreateDirectory(canonicalRoot!);
        string workspace = Path.Combine(canonicalRoot!, executionId.Value.ToString("N"));
        string recordDirectory = Path.Combine(workspace,
            recordId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(recordDirectory);
        string destination = Path.Combine(recordDirectory, $"book.{format.Format.ToLowerInvariant()}");

        await using (FileStream input = new(source!, FileMode.Open, FileAccess.Read, FileShare.Read,
                         128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }

        FileInfo staged = new(destination);
        if (staged.Length != format.Fingerprint.SizeInBytes
            || await CalibreToolDiscovery.HashFileAsync(destination, cancellationToken).ConfigureAwait(false)
                != format.Fingerprint.Sha256)
        {
            File.Delete(destination);
            throw new IOException("The staged format does not match the scanned source fingerprint.");
        }
        return new(recordId, format.Format, destination, format.Fingerprint);
    }

    public Task CleanupAsync(
        CleanupExecutionId executionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string workspace = Path.Combine(Path.GetFullPath(options.TransferStagingRoot),
            executionId.Value.ToString("N"));
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        return Task.CompletedTask;
    }
}
