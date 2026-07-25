using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Calibre;

internal sealed class CalibreCommandGateway(
    CalibreExecutionOptions options,
    DirectCalibreProcessRunner processRunner) : ICalibreCommandGateway, IRecoveryCalibreGateway
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

    public async Task<RecoveryCalibreCommandResult> CreateEmptyRecordAsync(
        CreateRecoveryRecordCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Supports(request.Tool, request.Profile, RecoveryCapability.CreateEmptyRecord)
            || !request.Profile.Supports(RecoveryCapability.FindCreatedRecord))
            return RecoveryFailed("add-empty", "CALIBRE_RECOVERY_CAPABILITY_DISABLED");
        ToolBoundaryValidation validation = await ValidateToolAndLibraryAsync(
            request.Tool, request.LibraryRoot, CalibreExecutionCapability.ExportRecord,
            cancellationToken).ConfigureAwait(false);
        await using FileStream? executableLock = validation.ExecutableLock;
        if (validation.FailureCode is not null)
            return RecoveryFailed("add-empty", validation.FailureCode);
        string authors = string.Join(" & ", request.Authors);
        string[] arguments =
        [
            "--with-library", validation.CanonicalLibraryRoot!,
            "add", "--empty", "--title", request.Title, "--authors", authors,
        ];
        CalibreCommandResult result = await processRunner.RunAsync(
            request.Tool.CanonicalExecutablePath, validation.CanonicalLibraryRoot!, "add-empty",
            arguments, [validation.CanonicalLibraryRoot!], false, null,
            CancellationToken.None).ConfigureAwait(false);
        return Convert(result, TryReadCreatedId(result));
    }

    public async Task<RecoveryCalibreCommandResult> SetMetadataFieldAsync(
        SetRecoveryMetadataFieldCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RecoveryCapability capability = MetadataCapability(request.Field);
        if (!Supports(request.Tool, request.Profile, capability))
            return RecoveryFailed("set_metadata", "CALIBRE_RECOVERY_CAPABILITY_DISABLED");
        ToolBoundaryValidation validation = await ValidateToolAndLibraryAsync(
            request.Tool, request.LibraryRoot, CalibreExecutionCapability.ExportRecord,
            cancellationToken).ConfigureAwait(false);
        await using FileStream? executableLock = validation.ExecutableLock;
        if (validation.FailureCode is not null)
            return RecoveryFailed("set_metadata", validation.FailureCode);
        string field = MetadataField(request.Field);
        string separator = request.Field == RecoveryCalibreMetadataField.Authors ? " & " : ",";
        string value = string.Join(separator, request.Values);
        string[] arguments =
        [
            "--with-library", validation.CanonicalLibraryRoot!,
            "set_metadata", request.RecordId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "--field", $"{field}:{value}",
        ];
        return Convert(await processRunner.RunAsync(
            request.Tool.CanonicalExecutablePath, validation.CanonicalLibraryRoot!, "set_metadata",
            arguments, [validation.CanonicalLibraryRoot!], false, null,
            CancellationToken.None).ConfigureAwait(false));
    }

    public async Task<RecoveryCalibreCommandResult> AddOrReplaceFormatAsync(
        RestoreRecoveryFormatCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RecoveryCapability capability = request.ReplacesExisting
            ? RecoveryCapability.ReplaceExistingFormat
            : RecoveryCapability.AddBackedUpFormat;
        if (!Supports(request.Tool, request.Profile, capability))
            return RecoveryFailed("add_format", "CALIBRE_RECOVERY_CAPABILITY_DISABLED");
        AddOrReplaceCalibreFormatRequest command = new(
            request.Tool, request.LibraryRoot, request.RecordId, request.CanonicalFormat,
            request.VerifiedOriginalBackupPhysicalIdentity, request.ExpectedFingerprint);
        CalibreCommandResult result = await AddOrReplaceFormatAsync(command,
            cancellationToken).ConfigureAwait(false);
        return Convert(result);
    }

    public async Task<RecoveryCalibreCommandResult> RemoveFormatAsync(
        RemoveRecoveryFormatCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Supports(request.Tool, request.Profile, RecoveryCapability.RemoveCleanupAddedFormat))
            return RecoveryFailed("remove_format", "CALIBRE_RECOVERY_CAPABILITY_DISABLED");
        ToolBoundaryValidation validation = await ValidateToolAndLibraryAsync(
            request.Tool, request.LibraryRoot, CalibreExecutionCapability.ExportRecord,
            cancellationToken).ConfigureAwait(false);
        await using FileStream? executableLock = validation.ExecutableLock;
        if (validation.FailureCode is not null)
            return RecoveryFailed("remove_format", validation.FailureCode);
        string format = request.CanonicalFormat.ToUpperInvariant();
        if (format.Length == 0 || !format.All(char.IsAsciiLetterOrDigit))
            return RecoveryFailed("remove_format", "CALIBRE_FORMAT_INVALID");
        string[] arguments =
        [
            "--with-library", validation.CanonicalLibraryRoot!,
            "remove_format", request.RecordId.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture), format,
        ];
        return Convert(await processRunner.RunAsync(
            request.Tool.CanonicalExecutablePath, validation.CanonicalLibraryRoot!, "remove_format",
            arguments, [validation.CanonicalLibraryRoot!], false, null,
            CancellationToken.None).ConfigureAwait(false));
    }

    public async Task<RecoveryCalibreCommandResult> RemoveRecordAsync(
        RemoveRecoveryRecordCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Supports(request.Tool, request.Profile, RecoveryCapability.RemoveCleanupCreatedRecord))
            return RecoveryFailed("remove", "CALIBRE_RECOVERY_CAPABILITY_DISABLED");
        RemoveCalibreRecordRequest command = new(
            request.Tool, request.LibraryRoot, request.RecordId);
        CalibreCommandResult result = await RemoveRecordAsync(command,
            cancellationToken).ConfigureAwait(false);
        return Convert(result);
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
        if (!options.IsValidatedCompatibilityProfileEnabled
            || !tool.Capabilities.Contains(requiredCapability)
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

    private bool Supports(
        CalibreToolDescriptor tool,
        RecoveryCapabilityProfile profile,
        RecoveryCapability capability) =>
        options.IsValidatedRecoveryProfileEnabled
        && profile.ToolIdentity == tool.Identity
        && profile.ProfileIdentity == $"{options.CapabilityProfile}/recovery/1.0"
        && profile.Supports(capability)
        && options.EnabledRecoveryCapabilities.Contains(capability);

    private static RecoveryCapability MetadataCapability(RecoveryCalibreMetadataField field) => field switch
    {
        RecoveryCalibreMetadataField.Title => RecoveryCapability.RestoreTitle,
        RecoveryCalibreMetadataField.Authors => RecoveryCapability.RestoreAuthors,
        RecoveryCalibreMetadataField.AuthorSort => RecoveryCapability.RestoreAuthorSort,
        RecoveryCalibreMetadataField.Publisher => RecoveryCapability.RestorePublisher,
        RecoveryCalibreMetadataField.PublicationDate => RecoveryCapability.RestorePublicationDate,
        RecoveryCalibreMetadataField.Languages => RecoveryCapability.RestoreLanguages,
        RecoveryCalibreMetadataField.Identifiers => RecoveryCapability.RestoreIdentifiers,
        RecoveryCalibreMetadataField.Series => RecoveryCapability.RestoreSeries,
        RecoveryCalibreMetadataField.SeriesIndex => RecoveryCapability.RestoreSeriesIndex,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static string MetadataField(RecoveryCalibreMetadataField field) => field switch
    {
        RecoveryCalibreMetadataField.Title => "title",
        RecoveryCalibreMetadataField.Authors => "authors",
        RecoveryCalibreMetadataField.AuthorSort => "author_sort",
        RecoveryCalibreMetadataField.Publisher => "publisher",
        RecoveryCalibreMetadataField.PublicationDate => "pubdate",
        RecoveryCalibreMetadataField.Languages => "languages",
        RecoveryCalibreMetadataField.Identifiers => "identifiers",
        RecoveryCalibreMetadataField.Series => "series",
        RecoveryCalibreMetadataField.SeriesIndex => "series_index",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static RecoveryCalibreCommandResult Convert(
        CalibreCommandResult result,
        CalibreBookId? createdId = null) =>
        new(result.CommandKind, result.Started, result.ExitCode,
            result.SanitizedArguments, result.SanitizedStandardOutput,
            result.SanitizedStandardError, result.Duration, createdId, result.FailureCode);

    private static RecoveryCalibreCommandResult RecoveryFailed(string command, string code) =>
        new(command, false, null, [], string.Empty, string.Empty, TimeSpan.Zero,
            FailureCode: code);

    private static CalibreBookId? TryReadCreatedId(CalibreCommandResult result)
    {
        if (!result.IsSuccess) return null;
        string text = result.SanitizedStandardOutput + "\n" + result.SanitizedStandardError;
        System.Text.RegularExpressions.Match match =
            System.Text.RegularExpressions.Regex.Match(text,
                @"\b(?:book\s+ids?|id)\D{0,16}(\d+)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success && long.TryParse(match.Groups[1].Value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out long id) && id > 0
            ? new(id) : null;
    }
}
