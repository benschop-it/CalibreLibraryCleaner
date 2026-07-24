using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Calibre;

internal sealed class CalibreCommandGateway(
    CalibreExecutionOptions options,
    DirectCalibreProcessRunner processRunner) : ICalibreCommandGateway
{
    public async Task<CalibreCommandResult> ExportRecordAsync(
        ExportCalibreRecordRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ToolBoundaryValidation validation = await ValidateToolAndLibraryAsync(request.Tool, request.LibraryRoot,
            CalibreExecutionCapability.ExportRecord, cancellationToken).ConfigureAwait(false);
        await using FileStream? executableLock = validation.ExecutableLock;
        if (validation.FailureCode is not null) return Failed("export", validation.FailureCode);
        if (!Directory.Exists(request.DestinationDirectory)
            || !ExecutionPathGuard.TryValidateExternalDirectory(validation.CanonicalLibraryRoot!, request.DestinationDirectory, true, out string? destination, out _)
            || Directory.EnumerateFileSystemEntries(destination!).Any())
            return Failed("export", "CALIBRE_EXPORT_DESTINATION_INVALID");
        string[] arguments =
        [
            "--with-library", validation.CanonicalLibraryRoot!,
            "export", "--dont-update-metadata", "--to-dir", destination!, "--single-dir",
            request.RecordId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];
        return await processRunner.RunAsync(request.Tool.CanonicalExecutablePath, validation.CanonicalLibraryRoot!, "export", arguments,
            [validation.CanonicalLibraryRoot!, destination!], true, options.ReadOnlyCommandTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CalibreCommandResult> AddOrReplaceFormatAsync(
        AddOrReplaceCalibreFormatRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ToolBoundaryValidation validation = await ValidateToolAndLibraryAsync(request.Tool, request.LibraryRoot,
            CalibreExecutionCapability.AddOrReplaceFormat, cancellationToken).ConfigureAwait(false);
        await using FileStream? executableLock = validation.ExecutableLock;
        if (validation.FailureCode is not null) return Failed("add_format", validation.FailureCode);
        string format = request.CanonicalFormat?.ToUpperInvariant() ?? string.Empty;
        string backupPath;
        try { backupPath = Path.GetFullPath(request.VerifiedBackupFilePath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failed("add_format", "CALIBRE_BACKUP_INPUT_INVALID");
        }
        if (format.Length == 0 || !format.All(char.IsAsciiLetterOrDigit)
            || !File.Exists(backupPath) || ExecutionPathGuard.IsContained(validation.CanonicalLibraryRoot!, backupPath)
            || !ExecutionPathGuard.TryRejectReparsePoints(backupPath, true, out _)
            || !string.Equals(Path.GetExtension(backupPath), $".{format}", StringComparison.OrdinalIgnoreCase))
            return Failed("add_format", "CALIBRE_BACKUP_INPUT_INVALID");
        FileStream backupLock;
        try
        {
            backupLock = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (backupLock.Length != request.ExpectedFingerprint.SizeInBytes
                || await CalibreToolDiscovery.HashStreamAsync(backupLock, cancellationToken).ConfigureAwait(false)
                != request.ExpectedFingerprint.Sha256)
            {
                await backupLock.DisposeAsync().ConfigureAwait(false);
                return Failed("add_format", "CALIBRE_BACKUP_INPUT_CHANGED");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failed("add_format", "CALIBRE_BACKUP_INPUT_INVALID");
        }
        await using (backupLock.ConfigureAwait(false))
        {
            string[] arguments =
            [
                "--with-library", validation.CanonicalLibraryRoot!,
                "add_format", request.TargetRecordId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), backupPath,
            ];
            return await processRunner.RunAsync(request.Tool.CanonicalExecutablePath, validation.CanonicalLibraryRoot!, "add_format", arguments,
                [validation.CanonicalLibraryRoot!, backupPath], false, null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<CalibreCommandResult> RemoveRecordAsync(
        RemoveCalibreRecordRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ToolBoundaryValidation validation = await ValidateToolAndLibraryAsync(request.Tool, request.LibraryRoot,
            CalibreExecutionCapability.RemoveRecordNonPermanently, cancellationToken).ConfigureAwait(false);
        await using FileStream? executableLock = validation.ExecutableLock;
        if (validation.FailureCode is not null) return Failed("remove", validation.FailureCode);
        string[] arguments =
        [
            "--with-library", validation.CanonicalLibraryRoot!,
            "remove", request.RecordId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];
        return await processRunner.RunAsync(request.Tool.CanonicalExecutablePath, validation.CanonicalLibraryRoot!, "remove", arguments,
            [validation.CanonicalLibraryRoot!], false, null, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<ToolBoundaryValidation> ValidateToolAndLibraryAsync(
        CalibreToolDescriptor tool,
        string libraryRoot,
        CalibreExecutionCapability requiredCapability,
        CancellationToken cancellationToken)
    {
        string trustedPath;
        string toolPath;
        try
        {
            trustedPath = Path.GetFullPath(options.TrustedExecutablePath);
            toolPath = Path.GetFullPath(tool.CanonicalExecutablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return FailedValidation("CALIBRE_TOOL_IDENTITY_INVALID");
        }
        if (!tool.Capabilities.Contains(requiredCapability)
            || tool.Identity.ProductVersion != options.SupportedVersion
            || tool.Identity.CapabilityProfile != options.CapabilityProfile
            || !string.Equals(toolPath, trustedPath, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(tool.CanonicalExecutablePath)
            || !ExecutionPathGuard.TryRejectReparsePoints(tool.CanonicalExecutablePath, true, out _))
            return FailedValidation("CALIBRE_TOOL_IDENTITY_INVALID");
        FileStream? executableLock = null;
        try
        {
            executableLock = new FileStream(tool.CanonicalExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (await CalibreToolDiscovery.HashStreamAsync(executableLock, cancellationToken).ConfigureAwait(false)
                != tool.Identity.ExecutableSha256)
            {
                await executableLock.DisposeAsync().ConfigureAwait(false);
                return FailedValidation("CALIBRE_TOOL_IDENTITY_CHANGED");
            }
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
            string database = Path.Combine(root, "metadata.db");
            if (!Directory.Exists(root) || !File.Exists(database)
                || !ExecutionPathGuard.TryRejectReparsePoints(root, true, out _)
                || (File.GetAttributes(database) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                await executableLock.DisposeAsync().ConfigureAwait(false);
                return FailedValidation("CALIBRE_LIBRARY_PATH_INVALID");
            }
            if (!ExecutionPathGuard.TryValidateExternalDirectory(
                    root, options.ControlledConfigDirectory, false, out _, out _))
            {
                await executableLock.DisposeAsync().ConfigureAwait(false);
                return FailedValidation("CALIBRE_CONFIG_LOCATION_UNSAFE");
            }
            return new(null, root, executableLock);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            if (executableLock is not null) await executableLock.DisposeAsync().ConfigureAwait(false);
            return FailedValidation("CALIBRE_BOUNDARY_VALIDATION_FAILED");
        }
    }

    private static ToolBoundaryValidation FailedValidation(string code) => new(code, null, null);
    private sealed record ToolBoundaryValidation(
        string? FailureCode,
        string? CanonicalLibraryRoot,
        FileStream? ExecutableLock);

    private static CalibreCommandResult Failed(string command, string code) =>
        new(command, false, null, [], string.Empty, string.Empty, TimeSpan.Zero, code);
}
