using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Infrastructure.Plans;

namespace CalibreLibraryCleaner.Infrastructure.Execution;

internal sealed class FileExecutionBackupStore : IExecutionBackupStore
{
    private const int BufferSize = 128 * 1024;
    private const long MaximumOpfBytes = 16L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public async Task<BackupDestinationValidation> ValidateDestinationAsync(
        string libraryRoot,
        string backupDestination,
        long requiredBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<ExecutionIssue> issues = [];
        if (!ExecutionPathGuard.TryValidateExternalDirectory(libraryRoot, backupDestination, true,
                out string? canonical, out string? reason))
            return Invalid("EXECUTION.BACKUP_DESTINATION_UNSAFE", reason ?? "The external backup destination is unsafe.");
        long available;
        try
        {
            string? root = Path.GetPathRoot(canonical!);
            if (string.IsNullOrWhiteSpace(root)) return Invalid("EXECUTION.BACKUP_SPACE_UNKNOWN", "Available backup space cannot be determined.");
            available = new DriveInfo(root).AvailableFreeSpace;
            if (available < requiredBytes)
                issues.Add(Block("EXECUTION.BACKUP_SPACE_INSUFFICIENT", "The external destination does not have the conservative required free space."));
            string probe = Path.Combine(canonical!, $".calibre-library-cleaner-write-probe-{Guid.NewGuid():N}");
            await using (FileStream stream = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1, FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.DeleteOnClose))
            {
                await stream.WriteAsync(new byte[] { 0x00 }, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Invalid("EXECUTION.BACKUP_DESTINATION_INACCESSIBLE", "The external backup destination cannot be written and durably verified.");
        }
        return new(canonical, available, issues);
    }

    public Task<ExecutionWorkspace> CreateWorkspaceAsync(
        CleanupExecutionId executionId,
        string canonicalBackupDestinationIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string parent = Path.GetFullPath(canonicalBackupDestinationIdentity);
        if (!Directory.Exists(parent) || !ExecutionPathGuard.TryRejectReparsePoints(parent, true, out _))
            throw new IOException("The verified backup destination is no longer available.");
        string bundle = Path.Combine(parent, $"execution-{executionId}");
        if (Directory.Exists(bundle) || File.Exists(bundle))
            throw new IOException("The execution workspace already exists.");
        Directory.CreateDirectory(bundle);
        if (!ExecutionPathGuard.TryRejectReparsePoints(bundle, true, out _))
            throw new IOException("The execution workspace is not a physical directory.");
        return Task.FromResult(new ExecutionWorkspace(executionId, bundle, parent));
    }

    public async Task<ExecutionBackupInputs> CreateInputsAsync(
        CreateBackupInputsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<ExecutionIssue> issues = [];
        Dictionary<CalibreBookId, string> exportDirectories = [];
        Dictionary<BackupFormatKey, string> rawPaths = [];
        try
        {
            byte[] planBytes = CleanupPlanJsonSerializer.Serialize(request.Plan);
            await WriteCreateNewAsync(Path.Combine(request.Workspace.BundlePath, "approved.cleanup-plan.json"), planBytes, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(Path.Combine(request.Workspace.BundlePath, "tool-identity.json"), new
            {
                request.Tool.CanonicalExecutablePath,
                productVersion = request.Tool.Identity.ProductVersion,
                executableSha256 = request.Tool.Identity.ExecutableSha256.Value,
                capabilityProfile = request.Tool.Identity.CapabilityProfile,
                capabilities = request.Tool.Capabilities.Select(value => value.ToString()).Order(StringComparer.Ordinal).ToArray(),
            }, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(Path.Combine(request.Workspace.BundlePath, "application-identity.json"), new
            {
                applicationVersion = request.ApplicationVersion,
            }, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(Path.Combine(request.Workspace.BundlePath, "local-execution-confirmation.json"), new
            {
                planId = request.Confirmation.PlanId.ToString(),
                planRevision = request.Confirmation.PlanRevision.Value,
                planContentDigest = request.Confirmation.PlanContentDigest.Value,
                request.Confirmation.LibraryUuid,
                request.Confirmation.CanonicalLibraryRootIdentity,
                operationGraphDigest = request.Confirmation.OperationGraphDigest.Value,
                toolIdentity = request.Confirmation.ToolIdentity,
                request.Confirmation.BackupDestinationIdentity,
                request.Confirmation.ConfirmedAtUtc,
                request.Confirmation.OtherCalibreMutatorsClosed,
                request.Confirmation.RecoveryLimitationsAccepted,
            }, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(Path.Combine(request.Workspace.BundlePath, "preflight-evidence.json"), new
            {
                planId = request.Plan.Id.ToString(),
                planRevision = request.Plan.ArtifactRevision.Value,
                planContentDigest = request.Plan.ContentDigest.Value,
                libraryUuid = request.Plan.InputIdentity.LibraryUuid,
                unaffectedBaselineSha256 = request.UnaffectedBaseline.Value,
                createdAtUtc = request.CreatedAtUtc.ToUniversalTime(),
            }, cancellationToken).ConfigureAwait(false);

            ManagedStateItem[] managedState = request.Plan.Definition.ExpectedLibraryState.Records
                .SelectMany(value => value.Formats)
                .OrderBy(value => value.RecordId.Value).ThenBy(value => value.Format, StringComparer.Ordinal)
                .Select(value => new ManagedStateItem(value.RecordId.Value, value.Format, value.StoredFileName,
                    value.RelativePath, value.Fingerprint.SizeInBytes, value.Fingerprint.Sha256.Value,
                    value.Observation.CreationTimeUtc, value.Observation.LastWriteTimeUtc, value.Observation.Attributes,
                    value.ObservationSourceVersion)).ToArray();
            await WriteJsonAsync(Path.Combine(request.Workspace.BundlePath, "managed-state.json"), managedState, cancellationToken).ConfigureAwait(false);

            string rawRoot = Path.Combine(request.Workspace.BundlePath, "raw-formats");
            string exportRoot = Path.Combine(request.Workspace.BundlePath, "exports");
            Directory.CreateDirectory(rawRoot);
            Directory.CreateDirectory(exportRoot);
            foreach (ExpectedRecordState record in request.Plan.Definition.ExpectedLibraryState.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string recordRaw = Path.Combine(rawRoot, record.RecordId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                string recordExport = Path.Combine(exportRoot, record.RecordId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(recordRaw);
                Directory.CreateDirectory(recordExport);
                exportDirectories.Add(record.RecordId, recordExport);
                foreach (ExpectedFormatState format in record.Formats)
                {
                    if (!ExecutionPathGuard.TryValidateContainedRegularFile(request.LibraryRoot, format.RelativePath,
                            out string? source, out string? reason))
                    {
                        issues.Add(Block("EXECUTION.BACKUP_SOURCE_UNSAFE", reason ?? "A managed format source is unsafe.", record.RecordId, format.Format));
                        continue;
                    }
                    string destination = Path.Combine(recordRaw, $"book.{format.Format.ToLowerInvariant()}");
                    ExecutionIssue? copyIssue = await CopyAndVerifyExpectedAsync(source!, destination, format, cancellationToken).ConfigureAwait(false);
                    if (copyIssue is not null) issues.Add(copyIssue);
                    else rawPaths.Add(new(record.RecordId, format.Format, format.RelativePath), destination);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            issues.Add(Block("EXECUTION.BACKUP_CREATION_FAILED", "The external backup inputs could not be created completely."));
        }
        return new(request.Workspace, exportDirectories, rawPaths, issues);
    }

    public async Task<ExecutionBackupResult> VerifyAndSealAsync(
        SealBackupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<ExecutionIssue> issues = [];
        List<BackupManifestEntry> entries = [];
        string bundle = request.Inputs.Workspace.BundlePath;
        try
        {
            string planPath = Path.Combine(bundle, "approved.cleanup-plan.json");
            byte[] planBytes = await ReadBoundedAsync(planPath, 64L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            CleanupPlanStoreReadResult restoredPlan = CleanupPlanJsonSerializer.Deserialize(planBytes);
            if (!restoredPlan.IsSuccess || restoredPlan.Plan!.Id != request.Plan.Id
                || restoredPlan.Plan.ContentDigest != request.Plan.ContentDigest
                || restoredPlan.Plan.ArtifactRevision != request.Plan.ArtifactRevision)
                issues.Add(Block("EXECUTION.BACKUP_PLAN_INVALID", "The backed-up cleanup plan cannot be reconstructed as the approved artifact."));
            await AddEntryAsync(entries, bundle, planPath, BackupArtifactKind.CleanupPlan,
                Requirements(request.Plan, BackupRequirementKind.CleanupPlanArtifact), null, null, cancellationToken).ConfigureAwait(false);
            await AddEntryAsync(entries, bundle, Path.Combine(bundle, "tool-identity.json"), BackupArtifactKind.ToolIdentity, [], null, null, cancellationToken).ConfigureAwait(false);
            await AddEntryAsync(entries, bundle, Path.Combine(bundle, "application-identity.json"), BackupArtifactKind.ApplicationIdentity, [], null, null, cancellationToken).ConfigureAwait(false);
            await AddEntryAsync(entries, bundle, Path.Combine(bundle, "preflight-evidence.json"), BackupArtifactKind.PreflightEvidence, [], null, null, cancellationToken).ConfigureAwait(false);
            await AddEntryAsync(entries, bundle, Path.Combine(bundle, "local-execution-confirmation.json"),
                BackupArtifactKind.LocalExecutionConfirmation, [], null, null, cancellationToken).ConfigureAwait(false);
            await AddEntryAsync(entries, bundle, Path.Combine(bundle, "managed-state.json"), BackupArtifactKind.ManagedState,
                Requirements(request.Plan, BackupRequirementKind.ManagedPathAndFileState), null, null, cancellationToken).ConfigureAwait(false);

            foreach (ExpectedRecordState record in request.Plan.Definition.ExpectedLibraryState.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (ExpectedFormatState format in record.Formats)
                {
                    BackupFormatKey key = new(record.RecordId, format.Format, format.RelativePath);
                    if (!request.Inputs.RawFormatBackupPaths.TryGetValue(key, out string? rawPath)
                        || !await FileMatchesAsync(rawPath, format.Fingerprint, cancellationToken).ConfigureAwait(false))
                    {
                        issues.Add(Block("EXECUTION.BACKUP_RAW_FORMAT_INVALID", "A raw format backup is absent or has a hash mismatch.", record.RecordId, format.Format));
                        continue;
                    }
                    await AddEntryAsync(entries, bundle, rawPath, BackupArtifactKind.RawFormat,
                        Requirements(request.Plan, BackupRequirementKind.FormatFile, record.RecordId, format.Format),
                        record.RecordId, format.Format, cancellationToken).ConfigureAwait(false);
                }

                if (!request.Inputs.ExportDirectories.TryGetValue(record.RecordId, out string? exportDirectory)
                    || !Directory.Exists(exportDirectory))
                {
                    issues.Add(Block("EXECUTION.BACKUP_EXPORT_MISSING", "An affected record export is missing.", record.RecordId));
                    continue;
                }
                await ClassifyExportAsync(request.Plan, record, exportDirectory, bundle, entries, issues, cancellationToken).ConfigureAwait(false);
            }

            if (issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError))
                return new(null, request.Inputs.RawFormatBackupPaths, issues);
            VerifiedBackupManifest manifest = VerifiedBackupManifest.Create(request.Inputs.Workspace.ExecutionId,
                request.Plan, request.VerifiedAtUtc, entries);
            issues.AddRange(BackupManifestCoveragePolicy.Validate(request.Plan, manifest));
            if (issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError))
                return new(null, request.Inputs.RawFormatBackupPaths, issues);
            string manifestPath = Path.Combine(bundle, "backup-manifest.json");
            await WriteJsonAsync(manifestPath, ToDto(manifest), cancellationToken).ConfigureAwait(false);
            VerifiedBackupManifest restored = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (!ManifestEquivalent(restored, manifest))
                issues.Add(Block("EXECUTION.BACKUP_MANIFEST_REOPEN_FAILED", "The sealed backup manifest did not survive durable reread."));
            issues.AddRange(await VerifyAvailableAsync(request.Inputs.Workspace, manifest, cancellationToken).ConfigureAwait(false));
            return issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError)
                ? new(null, request.Inputs.RawFormatBackupPaths, issues)
                : new(manifest, request.Inputs.RawFormatBackupPaths, issues);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or XmlException or ArgumentException or InvalidOperationException)
        {
            issues.Add(Block("EXECUTION.BACKUP_VERIFICATION_FAILED", "The external backup could not be completely and durably verified."));
            return new(null, request.Inputs.RawFormatBackupPaths, issues);
        }
    }

    public async Task<IReadOnlyList<ExecutionIssue>> VerifyAvailableAsync(
        ExecutionWorkspace workspace,
        VerifiedBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        List<ExecutionIssue> issues = [];
        try
        {
            string manifestPath = Path.Combine(workspace.BundlePath, "backup-manifest.json");
            if (!File.Exists(manifestPath)
                || !ExecutionPathGuard.TryRejectReparsePoints(manifestPath, true, out _)
                || !ManifestEquivalent(await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false), manifest))
                issues.Add(Block("EXECUTION.BACKUP_MANIFEST_UNAVAILABLE", "The sealed backup manifest is absent or changed."));
            foreach (BackupManifestEntry entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(Path.Combine(workspace.BundlePath, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!ExecutionPathGuard.IsContained(workspace.BundlePath, path)
                    || !File.Exists(path)
                    || !ExecutionPathGuard.TryRejectReparsePoints(path, true, out _)
                    || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                    || new FileInfo(path).Length != entry.SizeInBytes
                    || await HashFileAsync(path, cancellationToken).ConfigureAwait(false) != entry.Sha256)
                    issues.Add(Block("EXECUTION.BACKUP_ARTIFACT_CHANGED", "A verified backup artifact is missing or changed."));
            }
            string journal = Path.Combine(workspace.BundlePath, "execution.journal.jsonl");
            if (!File.Exists(journal)
                || !ExecutionPathGuard.TryRejectReparsePoints(journal, true, out _)
                || new FileInfo(journal).Length == 0)
                issues.Add(Block("EXECUTION.JOURNAL_UNAVAILABLE", "The durable execution journal is no longer available."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            issues.Add(Block("EXECUTION.BACKUP_AVAILABILITY_UNKNOWN", "The verified backup and journal could not be revalidated."));
        }
        return issues;
    }

    private static async Task ClassifyExportAsync(
        CleanupPlan plan,
        ExpectedRecordState record,
        string exportDirectory,
        string bundle,
        List<BackupManifestEntry> entries,
        List<ExecutionIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!ExecutionPathGuard.TryRejectReparsePoints(exportDirectory, true, out _))
        {
            issues.Add(Block("EXECUTION.BACKUP_EXPORT_UNSAFE", "An exported record directory is linked or unsafe.", record.RecordId));
            return;
        }
        string[] entriesInExport = Directory.EnumerateFileSystemEntries(
            exportDirectory, "*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
        foreach (string directory in entriesInExport.Where(Directory.Exists))
        {
            if (!ExecutionPathGuard.TryRejectReparsePoints(directory, true, out _))
                issues.Add(Block("EXECUTION.BACKUP_EXPORT_UNSAFE", "An exported record contains a linked directory.", record.RecordId));
            issues.Add(Block("EXECUTION.UNMODELED_EXTRA_DATA",
                "The record export contains an unmodeled directory; V1 execution is blocked.", record.RecordId));
        }
        string[] files = entriesInExport.Where(File.Exists).ToArray();
        Dictionary<string, ExpectedFormatState> expectedFormats = record.Formats.ToDictionary(value => value.Format, StringComparer.Ordinal);
        HashSet<string> foundFormats = new(StringComparer.Ordinal);
        bool foundOpf = false;
        bool foundCover = false;
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ExecutionPathGuard.TryRejectReparsePoints(file, true, out _)
                || (File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                issues.Add(Block("EXECUTION.BACKUP_EXPORT_UNSAFE", "An exported payload is not a regular physical file.", record.RecordId));
                continue;
            }
            string name = Path.GetFileName(file);
            string extension = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
            if (string.Equals(extension, "OPF", StringComparison.Ordinal))
            {
                if (foundOpf || !await IsValidOpfAsync(file, cancellationToken).ConfigureAwait(false))
                    issues.Add(Block("EXECUTION.BACKUP_METADATA_INVALID", "The record export does not contain exactly one valid bounded OPF metadata file.", record.RecordId));
                foundOpf = true;
                await AddEntryAsync(entries, bundle, file, BackupArtifactKind.RecordMetadataOpf,
                    Requirements(plan, BackupRequirementKind.RecordMetadataSnapshot, record.RecordId), record.RecordId, null, cancellationToken).ConfigureAwait(false);
            }
            else if (string.Equals(name, "cover.jpg", StringComparison.OrdinalIgnoreCase))
            {
                if (foundCover || !record.HasCover || new FileInfo(file).Length <= 0)
                    issues.Add(Block("EXECUTION.BACKUP_COVER_INVALID", "The exported cover is unexpected, duplicate, or empty.", record.RecordId));
                foundCover = true;
                await AddEntryAsync(entries, bundle, file, BackupArtifactKind.Cover,
                    Requirements(plan, BackupRequirementKind.CoverIfPresent, record.RecordId), record.RecordId, null, cancellationToken).ConfigureAwait(false);
            }
            else if (expectedFormats.TryGetValue(extension, out ExpectedFormatState? expected))
            {
                if (!foundFormats.Add(extension) || !await FileMatchesAsync(file, expected.Fingerprint, cancellationToken).ConfigureAwait(false))
                    issues.Add(Block("EXECUTION.BACKUP_EXPORTED_FORMAT_INVALID", "An exported format is duplicate or differs from the approved bytes.", record.RecordId, extension));
                await AddEntryAsync(entries, bundle, file, BackupArtifactKind.ExportedFormat, [], record.RecordId, extension, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                issues.Add(Block("EXECUTION.UNMODELED_EXTRA_DATA", "The record export contains unmodeled extra data; V1 execution is blocked.", record.RecordId));
            }
        }
        if (!foundOpf) issues.Add(Block("EXECUTION.BACKUP_METADATA_MISSING", "The record export is missing OPF metadata.", record.RecordId));
        if (record.HasCover && !foundCover) issues.Add(Block("EXECUTION.BACKUP_COVER_MISSING", "The record export is missing the reported cover.", record.RecordId));
        foreach (ExpectedFormatState expected in record.Formats.Where(value => !foundFormats.Contains(value.Format)))
            issues.Add(Block("EXECUTION.BACKUP_EXPORTED_FORMAT_MISSING", "The record export is missing an ebook format.", record.RecordId, expected.Format));
    }

    private static async Task<ExecutionIssue?> CopyAndVerifyExpectedAsync(
        string source,
        string destination,
        ExpectedFormatState expected,
        CancellationToken cancellationToken)
    {
        try
        {
            FileInfo before = new(source);
            if (!ObservationMatches(before, expected))
                return Block("EXECUTION.BACKUP_SOURCE_CHANGED", "The managed source observation changed before backup.", expected.RecordId, expected.Format);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough);
            byte[] buffer = new byte[BufferSize];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total = checked(total + read);
            }
            output.Flush(flushToDisk: true);
            await output.DisposeAsync().ConfigureAwait(false);
            Sha256Digest sourceDigest = new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            FileInfo after = new(source); after.Refresh();
            if (total != expected.Fingerprint.SizeInBytes || sourceDigest != expected.Fingerprint.Sha256
                || !ObservationMatches(after, expected))
                return Block("EXECUTION.BACKUP_SOURCE_CHANGED", "The managed source changed while it was backed up.", expected.RecordId, expected.Format);
            if (!await FileMatchesAsync(destination, expected.Fingerprint, cancellationToken).ConfigureAwait(false))
                return Block("EXECUTION.BACKUP_HASH_MISMATCH", "The external backup copy failed independent hash verification.", expected.RecordId, expected.Format);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return Block("EXECUTION.BACKUP_COPY_FAILED", "The exact managed format could not be copied and verified externally.", expected.RecordId, expected.Format);
        }
    }

    private static bool ObservationMatches(FileInfo info, ExpectedFormatState expected) =>
        info.Exists && info.Length == expected.Observation.Length
        && new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero) == expected.Observation.CreationTimeUtc
        && new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) == expected.Observation.LastWriteTimeUtc
        && (int)info.Attributes == expected.Observation.Attributes;

    private static async Task<bool> FileMatchesAsync(string path, FormatFileFingerprint expected, CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        return info.Exists && info.Length == expected.SizeInBytes
            && await HashFileAsync(path, cancellationToken).ConfigureAwait(false) == expected.Sha256;
    }

    private static async Task<Sha256Digest> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant());
    }

    private static async Task<bool> IsValidOpfAsync(string path, CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumOpfBytes) return false;
        XmlReaderSettings settings = new()
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumOpfBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using XmlReader reader = XmlReader.Create(stream, settings);
        while (await reader.ReadAsync().ConfigureAwait(false)) cancellationToken.ThrowIfCancellationRequested();
        return true;
    }

    private static async Task AddEntryAsync(
        List<BackupManifestEntry> entries,
        string bundle,
        string path,
        BackupArtifactKind kind,
        IEnumerable<string> requirements,
        CalibreBookId? recordId,
        string? format,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string full = Path.GetFullPath(path);
        if (!ExecutionPathGuard.IsContained(bundle, full) || !File.Exists(full)
            || !ExecutionPathGuard.TryRejectReparsePoints(full, true, out _)
            || (File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("A backup artifact leaves the execution bundle or is not a physical file.");
        FileInfo info = new(full);
        Sha256Digest digest = await HashFileAsync(full, cancellationToken).ConfigureAwait(false);
        entries.Add(new(Path.GetRelativePath(bundle, full).Replace('\\', '/'), kind, info.Length, digest, requirements, recordId, format));
    }

    private static string[] Requirements(CleanupPlan plan, BackupRequirementKind kind, CalibreBookId? recordId = null, string? format = null) =>
        plan.Definition.BackupRequirements.Where(value => value.Kind == kind
                && (recordId is null || value.RecordId == recordId)
                && (format is null || value.Format == format))
            .Select(value => value.Id).Order(StringComparer.Ordinal).ToArray();

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        await WriteCreateNewAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), cancellationToken).ConfigureAwait(false);

    private static async Task WriteCreateNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, long maximumBytes, CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length < 0 || info.Length > maximumBytes) throw new IOException("A backup artifact exceeds its bound.");
        byte[] bytes = new byte[checked((int)info.Length)];
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static ManifestDto ToDto(VerifiedBackupManifest manifest) => new(
        VerifiedBackupManifest.Version, manifest.ExecutionId.ToString(), manifest.PlanId.ToString(),
        manifest.PlanContentDigest.Value, manifest.LibraryUuid, manifest.VerifiedAtUtc, manifest.ManifestDigest.Value,
        manifest.Entries.Select(value => new ManifestEntryDto(value.RelativePath, value.Kind, value.SizeInBytes,
            value.Sha256.Value, value.RequirementIds.ToArray(), value.RecordId?.Value, value.Format)).ToArray());

    private static bool ManifestEquivalent(VerifiedBackupManifest left, VerifiedBackupManifest right) =>
        left.ExecutionId == right.ExecutionId
        && left.PlanId == right.PlanId
        && left.PlanContentDigest == right.PlanContentDigest
        && string.Equals(left.LibraryUuid, right.LibraryUuid, StringComparison.Ordinal)
        && left.VerifiedAtUtc == right.VerifiedAtUtc
        && left.ManifestDigest == right.ManifestDigest
        && left.Entries.Count == right.Entries.Count
        && left.Entries.Zip(right.Entries).All(pair =>
            pair.First.RelativePath == pair.Second.RelativePath
            && pair.First.Kind == pair.Second.Kind
            && pair.First.SizeInBytes == pair.Second.SizeInBytes
            && pair.First.Sha256 == pair.Second.Sha256
            && pair.First.RecordId == pair.Second.RecordId
            && pair.First.Format == pair.Second.Format
            && pair.First.RequirementIds.SequenceEqual(pair.Second.RequirementIds));

    private static async Task<VerifiedBackupManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadBoundedAsync(path, 64L * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        ManifestDto dto = JsonSerializer.Deserialize<ManifestDto>(bytes, JsonOptions)
            ?? throw new JsonException("The backup manifest is empty.");
        if (dto.SchemaVersion != VerifiedBackupManifest.Version || !Guid.TryParse(dto.ExecutionId, out Guid executionId)
            || !Guid.TryParse(dto.PlanId, out Guid planId)) throw new JsonException("The backup manifest identity is invalid.");
        BackupManifestEntry[] entries = dto.Entries.Select(value => new BackupManifestEntry(value.RelativePath,
            value.Kind, value.SizeInBytes, new(value.Sha256), value.RequirementIds,
            value.RecordId is null ? null : new CalibreBookId(value.RecordId.Value), value.Format)).ToArray();
        return new(new(executionId), new(planId), new(dto.PlanContentDigest), dto.LibraryUuid,
            dto.VerifiedAtUtc, entries, new(dto.ManifestDigest));
    }

    private static BackupDestinationValidation Invalid(string code, string explanation) =>
        new(null, 0, [Block(code, explanation)]);
    private static ExecutionIssue Block(string code, string explanation, CalibreBookId? recordId = null, string? format = null) =>
        new(code, ExecutionIssueSeverity.BlockingError, explanation, recordId, format);

    private sealed record ManagedStateItem(
        long RecordId,
        string Format,
        string StoredFileName,
        string RelativePath,
        long SizeInBytes,
        string Sha256,
        DateTimeOffset CreationTimeUtc,
        DateTimeOffset LastWriteTimeUtc,
        int Attributes,
        string ObservationSourceVersion);

    private sealed record ManifestDto(
        string SchemaVersion,
        string ExecutionId,
        string PlanId,
        string PlanContentDigest,
        string LibraryUuid,
        DateTimeOffset VerifiedAtUtc,
        string ManifestDigest,
        ManifestEntryDto[] Entries);

    private sealed record ManifestEntryDto(
        string RelativePath,
        BackupArtifactKind Kind,
        long SizeInBytes,
        string Sha256,
        string[] RequirementIds,
        long? RecordId,
        string? Format);
}
