using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Recovery;

internal sealed class FileRecoveryStateBackupService(
    IExecutionBackupStore executionBackupStore,
    ICalibreCommandGateway calibre,
    IRecoveryPlanStore recoveryPlanStore) : IRecoveryStateBackupService
{
    private const string ManifestSchema = "cleanup-recovery-current-state-backup/1.0";
    private const int BufferSize = 128 * 1024;
    private const long MaximumManifestBytes = 64L * 1024 * 1024;
    private const long MaximumOpfBytes = 16L * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public async Task<RecoveryBackupDestinationValidation> ValidateDestinationAsync(
        string libraryRoot,
        string destination,
        long requiredBytes,
        CancellationToken cancellationToken)
    {
        BackupDestinationValidation value = await executionBackupStore.ValidateDestinationAsync(
            libraryRoot, destination, requiredBytes, cancellationToken).ConfigureAwait(false);
        return new(value.CanonicalDestinationIdentity, value.AvailableBytes,
            value.Issues.Select(ConvertIssue).ToArray());
    }

    public Task<string> CreateWorkspaceAsync(
        RecoveryExecutionId recoveryExecutionId,
        string canonicalDestinationIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string parent = Path.GetFullPath(canonicalDestinationIdentity);
        if (!Directory.Exists(parent)
            || !ExecutionPathGuard.TryRejectReparsePoints(parent, true, out _))
            throw new IOException("The recovery backup destination is no longer a physical directory.");
        string bundle = Path.Combine(parent, $"recovery-{recoveryExecutionId}");
        if (Directory.Exists(bundle) || File.Exists(bundle))
            throw new IOException("The recovery workspace already exists.");
        Directory.CreateDirectory(bundle);
        if (!ExecutionPathGuard.TryRejectReparsePoints(bundle, true, out _))
            throw new IOException("The recovery workspace cannot be proven physical.");
        return Task.FromResult(bundle);
    }

    public async Task<RecoveryCurrentStateBackupResult> CreateAndVerifyAsync(
        CreateRecoveryCurrentStateBackupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<RecoveryIssue> issues = [];
        List<RecoveryCurrentStateBackupArtifact> artifacts = [];
        Dictionary<(CalibreBookId RecordId, string Format), string> rawPaths = [];
        try
        {
            ValidateWorkspace(request);
            string planPath = Path.Combine(request.BundleIdentity, "approved.recovery-plan.json");
            RecoveryPlanStoreResult storedPlan = await recoveryPlanStore.WriteCreateNewAsync(
                request.Plan, planPath, request.LibraryRoot, cancellationToken).ConfigureAwait(false);
            issues.AddRange(storedPlan.Issues);
            if (!storedPlan.IsSuccess) return new(null, Ordered(issues));
            artifacts.Add(await DescribeAsync(request.BundleIdentity, planPath,
                RecoverySourceArtifactKind.RecoveryPlan, cancellationToken).ConfigureAwait(false));

            string chainPath = Path.Combine(request.BundleIdentity, "source-backup-chain.json");
            await WriteJsonCreateNewAsync(chainPath, new SourceChainDto(
                request.Plan.Id.ToString(), request.Plan.ContentDigest.Value,
                request.Plan.Definition.InputIdentity.SourcePlanId.ToString(),
                request.Plan.Definition.InputIdentity.SourcePlanContentDigest.Value,
                request.Plan.Definition.InputIdentity.SourceExecutionId.ToString(),
                request.Source.BundleIdentity,
                request.Plan.Definition.InputIdentity.SourceJournalFileDigest.Value,
                request.Plan.Definition.InputIdentity.SourceJournalFinalEntryHash,
                request.Plan.Definition.InputIdentity.SourceTerminalSummaryDigest?.Value,
                request.Plan.Definition.InputIdentity.OriginalManifestInternalDigest.Value,
                request.Plan.Definition.InputIdentity.OriginalManifestFileDigest.Value),
                cancellationToken).ConfigureAwait(false);
            artifacts.Add(await DescribeAsync(request.BundleIdentity, chainPath,
                RecoverySourceArtifactKind.OtherManifestEntry, cancellationToken).ConfigureAwait(false));
            string confirmationPath = Path.Combine(request.BundleIdentity,
                "recovery-execution-confirmation.json");
            await WriteJsonCreateNewAsync(confirmationPath, new ConfirmationDto(
                request.Confirmation.PlanId.ToString(),
                request.Confirmation.PlanRevision.Value,
                request.Confirmation.PlanContentDigest.Value,
                request.Confirmation.SourceExecutionId.ToString(),
                request.Confirmation.LibraryUuid,
                request.Confirmation.CanonicalRootIdentityDigest.Value,
                request.Confirmation.CurrentStateFingerprint.Value,
                request.Confirmation.CapabilityProfile,
                request.Confirmation.CurrentStateBackupDestinationIdentity,
                request.Confirmation.DestructiveOperationDigest.Value,
                request.Confirmation.OtherMutatorsClosed,
                request.Confirmation.SafeBoundaryCancellationUnderstood),
                cancellationToken).ConfigureAwait(false);
            artifacts.Add(await DescribeAsync(request.BundleIdentity, confirmationPath,
                RecoverySourceArtifactKind.OtherManifestEntry, cancellationToken).ConfigureAwait(false));
            await CopySourceAuditArtifactsAsync(request, artifacts, cancellationToken)
                .ConfigureAwait(false);

            HashSet<CalibreBookId> affected =
                PrepareRecoveryExecutionUseCase.AffectedRecordIds(request.Plan).ToHashSet();
            CalibreBook[] books = request.CurrentState.Snapshot.Books
                .Where(book => affected.Contains(book.Id)).OrderBy(book => book.Id.Value).ToArray();
            string inventoryPath = Path.Combine(request.BundleIdentity, "current-state-inventory.json");
            await WriteJsonCreateNewAsync(inventoryPath, books.Select(book => new InventoryRecordDto(
                book.Id.Value, book.Title, book.AuthorSort,
                book.Authors.Select(author => new InventoryAuthorDto(
                    author.Id.Value, author.Name, author.SortName)).ToArray(),
                book.Identifiers.OrderBy(value => value.Type, StringComparer.Ordinal)
                    .ThenBy(value => value.Value, StringComparer.Ordinal)
                    .Select(value => new InventoryIdentifierDto(value.Type, value.Value)).ToArray(),
                book.PublicationMetadata.Publisher,
                book.PublicationMetadata.PublicationDate,
                book.PublicationMetadata.Series,
                book.PublicationMetadata.SeriesIndex,
                book.PublicationMetadata.Languages.ToArray(),
                book.PublicationMetadata.HasCover,
                book.RelativeDirectory,
                book.Formats.OrderBy(value => value.Format, StringComparer.Ordinal)
                    .Select(format => new InventoryFormatDto(format.Format, format.StoredFileName,
                        format.ExpectedRelativePath, format.FileStatus,
                        format.Fingerprint?.SizeInBytes, format.Fingerprint?.Sha256.Value,
                        format.Observation?.Length, format.Observation?.CreationTimeUtc,
                        format.Observation?.LastWriteTimeUtc,
                        format.Observation?.Attributes)).ToArray()))
                .ToArray(), cancellationToken).ConfigureAwait(false);
            artifacts.Add(await DescribeAsync(request.BundleIdentity, inventoryPath,
                RecoverySourceArtifactKind.ManagedState, cancellationToken).ConfigureAwait(false));

            string rawRoot = Path.Combine(request.BundleIdentity, "current-raw-formats");
            string exportsRoot = Path.Combine(request.BundleIdentity, "current-exports");
            Directory.CreateDirectory(rawRoot);
            Directory.CreateDirectory(exportsRoot);
            foreach (CalibreBook book in books)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawRecord = Path.Combine(rawRoot, book.Id.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                string exportRecord = Path.Combine(exportsRoot, book.Id.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(rawRecord);
                Directory.CreateDirectory(exportRecord);
                LogicalRecoveryRecordId logicalRecordId =
                    LogicalRecordIdFor(request.Plan, book.Id);
                foreach (BookFormat format in book.Formats.OrderBy(value => value.Format, StringComparer.Ordinal))
                {
                    if (format.FileStatus != FormatFileStatus.Present
                        || format.Fingerprint is null
                        || !ExecutionPathGuard.TryValidateContainedRegularFile(
                            request.LibraryRoot, format.ExpectedRelativePath,
                            out string? source, out _))
                    {
                        issues.Add(Block("RECOVERY.CURRENT_FORMAT_UNREADABLE",
                            $"Current record {book.Id} format {format.Format}",
                            "Every current affected format must be readable and fingerprinted before recovery."));
                        continue;
                    }
                    string destination = Path.Combine(rawRecord,
                        $"book.{format.Format.ToLowerInvariant()}");
                    await CopyAndVerifyAsync(source!, destination, format.Fingerprint,
                        cancellationToken).ConfigureAwait(false);
                    RecoveryCurrentStateBackupArtifact artifact = await DescribeAsync(
                        request.BundleIdentity, destination, RecoverySourceArtifactKind.OriginalRawFormat,
                        cancellationToken, logicalRecordId, book.Id, format.Format,
                        PreservationRole(request.Plan, book.Id, format.Format)).ConfigureAwait(false);
                    artifacts.Add(artifact);
                    rawPaths.Add((book.Id, format.Format), destination);
                }

                CalibreCommandResult export = await calibre.ExportRecordAsync(new(
                    request.Tool, request.LibraryRoot, book.Id, exportRecord),
                    cancellationToken).ConfigureAwait(false);
                if (!export.IsSuccess)
                {
                    issues.Add(Block("RECOVERY.CURRENT_EXPORT_FAILED",
                        $"Current record {book.Id}",
                        "Calibre could not export current metadata and cover evidence."));
                    continue;
                }
                RecoveryCoverEvidence? cover = request.CurrentState.Covers.SingleOrDefault(
                    value => value.RecordId == book.Id);
                await CaptureExportsAsync(request.BundleIdentity, exportRecord, book,
                    logicalRecordId, cover,
                    artifacts, issues, cancellationToken).ConfigureAwait(false);
            }

            if (issues.Any(value => value.Severity == RecoveryIssueSeverity.Blocking))
                return new(null, Ordered(issues));
            RecoveryCurrentStateBackupArtifact[] ordered = artifacts
                .OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray();
            Sha256Digest internalDigest = ComputeInternalDigest(request, ordered);
            string manifestPath = Path.Combine(request.BundleIdentity, "current-state-backup-manifest.json");
            ManifestDto manifest = new(ManifestSchema, request.RecoveryExecutionId.ToString(),
                request.Plan.Id.ToString(), request.Plan.ContentDigest.Value,
                request.CurrentState.Snapshot.Identity.CalibreLibraryUuid,
                request.CurrentState.FullFingerprint.Value,
                request.CreatedAtUtc.ToUniversalTime(), internalDigest.Value,
                ordered.Select(ToDto).ToArray());
            await WriteJsonCreateNewAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
            Sha256Digest fileDigest = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            RecoveryCurrentStateBackup result = new(request.RecoveryExecutionId, request.Plan.Id,
                request.Plan.ContentDigest, request.CurrentState.Snapshot.Identity.CalibreLibraryUuid,
                request.BundleIdentity, manifestPath, fileDigest, internalDigest,
                request.CurrentState.FullFingerprint, ordered, rawPaths,
                request.Plan.Definition.InputIdentity.CanonicalRootIdentityDigest,
                request.Plan.Definition.InputIdentity.SourceJournalFileDigest,
                request.Plan.Definition.InputIdentity.OriginalManifestFileDigest);
            issues.AddRange(await VerifyAvailableAsync(result, cancellationToken).ConfigureAwait(false));
            return issues.Any(value => value.Severity == RecoveryIssueSeverity.Blocking)
                ? new(null, Ordered(issues))
                : new(result, Ordered(issues));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                   or JsonException or ArgumentException or InvalidOperationException
                   or NotSupportedException or CryptographicException)
        {
            issues.Add(Block("RECOVERY.CURRENT_BACKUP_FAILED", "Current-state backup",
                "The current affected state could not be backed up and independently verified."));
            return new(null, Ordered(issues));
        }
    }

    public async Task<IReadOnlyList<RecoveryIssue>> VerifyAvailableAsync(
        RecoveryCurrentStateBackup backup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backup);
        List<RecoveryIssue> issues = [];
        try
        {
            if (!File.Exists(backup.ManifestIdentity)
                || await HashFileAsync(backup.ManifestIdentity, cancellationToken).ConfigureAwait(false)
                    != backup.ManifestFileDigest)
                return [Block("RECOVERY.CURRENT_MANIFEST_CHANGED", "Current-state backup",
                    "The current-state backup manifest is missing or changed.")];
            ManifestDto manifest = JsonSerializer.Deserialize<ManifestDto>(
                await ReadBoundedAsync(backup.ManifestIdentity, cancellationToken).ConfigureAwait(false),
                Options) ?? throw new JsonException("The current-state manifest is empty.");
            if (manifest.SchemaVersion != ManifestSchema
                || manifest.RecoveryExecutionId != backup.RecoveryExecutionId.ToString()
                || manifest.RecoveryPlanId != backup.RecoveryPlanId.ToString()
                || manifest.RecoveryPlanContentDigest != backup.RecoveryPlanContentDigest.Value
                || manifest.LibraryUuid != backup.LibraryUuid
                || manifest.SourceStateFingerprint != backup.SourceStateFingerprint.Value
                || manifest.ManifestInternalDigest != backup.ManifestInternalDigest.Value
                || manifest.Entries.Length != backup.Artifacts.Count)
                issues.Add(Block("RECOVERY.CURRENT_MANIFEST_INVALID", "Current-state backup",
                    "The current-state manifest identities or inventory changed."));
            RecoveryCurrentStateBackupArtifact[] manifestEntries = manifest.Entries
                .Select(FromDto).OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray();
            if (!manifestEntries.SequenceEqual(backup.Artifacts)
                || ComputeInternalDigest(backup.RecoveryExecutionId, backup.RecoveryPlanId,
                    backup.RecoveryPlanContentDigest, backup.LibraryUuid,
                    backup.SourceStateFingerprint, manifestEntries) != backup.ManifestInternalDigest)
                issues.Add(Block("RECOVERY.CURRENT_MANIFEST_DIGEST_INVALID",
                    "Current-state backup",
                    "The manifest inventory does not reproduce its sealed internal digest."));

            foreach (RecoveryCurrentStateBackupArtifact entry in backup.Artifacts)
            {
                string path = Path.GetFullPath(Path.Combine(backup.BundleIdentity,
                    entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!ExecutionPathGuard.IsContained(backup.BundleIdentity, path)
                    || !File.Exists(path)
                    || !ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
                {
                    issues.Add(Block("RECOVERY.CURRENT_BACKUP_ITEM_MISSING",
                        entry.RelativePath, "A required current-state backup item is absent or unsafe."));
                    continue;
                }
                FileInfo info = new(path);
                if (info.Length != entry.SizeInBytes
                    || await HashFileAsync(path, cancellationToken).ConfigureAwait(false) != entry.Sha256)
                    issues.Add(Block("RECOVERY.CURRENT_BACKUP_ITEM_CHANGED",
                        entry.RelativePath, "A required current-state backup item has changed."));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                   or JsonException or ArgumentException or NotSupportedException)
        {
            issues.Add(Block("RECOVERY.CURRENT_BACKUP_UNVERIFIABLE", "Current-state backup",
                "The current-state backup could not be independently reverified."));
        }
        return Ordered(issues);
    }

    private static void ValidateWorkspace(CreateRecoveryCurrentStateBackupRequest request)
    {
        string bundle = Path.GetFullPath(request.BundleIdentity);
        string library = Path.GetFullPath(request.LibraryRoot);
        string destination = Path.GetFullPath(request.CanonicalDestinationIdentity);
        if (!Directory.Exists(bundle) || !ExecutionPathGuard.IsContained(destination, bundle)
            || ExecutionPathGuard.IsContained(library, bundle)
            || ExecutionPathGuard.IsContained(destination, library)
            || !ExecutionPathGuard.TryRejectReparsePoints(bundle, true, out _)
            || request.Plan.Definition.InputIdentity.CurrentLibraryUuid
                != request.CurrentState.Snapshot.Identity.CalibreLibraryUuid
            || request.Confirmation.PlanId != request.Plan.Id
            || request.Confirmation.PlanRevision != request.Plan.ArtifactRevision
            || request.Confirmation.PlanContentDigest != request.Plan.ContentDigest
            || request.Confirmation.SourceExecutionId
                != request.Plan.Definition.InputIdentity.SourceExecutionId
            || request.Confirmation.LibraryUuid
                != request.Plan.Definition.InputIdentity.CurrentLibraryUuid
            || request.Confirmation.CanonicalRootIdentityDigest
                != request.Plan.Definition.InputIdentity.CanonicalRootIdentityDigest
            || request.Confirmation.CurrentStateFingerprint
                != request.Plan.Definition.InputIdentity.FullStateFingerprint
            || request.Confirmation.CapabilityProfile
                != request.Plan.Definition.InputIdentity.RecoveryCapabilityProfile
            || !string.Equals(request.Confirmation.CurrentStateBackupDestinationIdentity,
                destination, StringComparison.OrdinalIgnoreCase)
            || request.Confirmation.DestructiveOperationDigest
                != ExecuteApprovedRecoveryPlanUseCase.ComputeDestructiveDigest(
                    request.Plan.Definition.OperationGraph)
            || !request.Confirmation.OtherMutatorsClosed
            || !request.Confirmation.SafeBoundaryCancellationUnderstood)
            throw new IOException("The recovery backup workspace or library identity is invalid.");
    }

    private static async Task CaptureExportsAsync(
        string bundle,
        string exportDirectory,
        CalibreBook book,
        LogicalRecoveryRecordId logicalRecordId,
        RecoveryCoverEvidence? cover,
        List<RecoveryCurrentStateBackupArtifact> artifacts,
        List<RecoveryIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!ExecutionPathGuard.TryRejectReparsePoints(exportDirectory, true, out _))
        {
            issues.Add(Block("RECOVERY.CURRENT_EXPORT_UNSAFE", $"Current record {book.Id}",
                "The Calibre export directory is linked or unsafe."));
            return;
        }
        string[] entries = Directory.EnumerateFileSystemEntries(
            exportDirectory, "*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).ToArray();
        if (entries.Any(Directory.Exists))
            issues.Add(Block("RECOVERY.CURRENT_EXPORT_EXTRA_DATA", $"Current record {book.Id}",
                "The current record export contains an unmodeled directory."));
        Dictionary<string, BookFormat> expectedFormats = book.Formats
            .Where(value => value.FileStatus == FormatFileStatus.Present && value.Fingerprint is not null)
            .ToDictionary(value => value.Format, StringComparer.Ordinal);
        HashSet<string> foundFormats = new(StringComparer.Ordinal);
        bool foundOpf = false;
        bool foundCover = false;
        foreach (string file in entries.Where(File.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ExecutionPathGuard.TryRejectReparsePoints(file, true, out _)
                || (File.GetAttributes(file) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                issues.Add(Block("RECOVERY.CURRENT_EXPORT_UNSAFE", $"Current record {book.Id}",
                    "An exported payload is not a regular physical file."));
                continue;
            }
            string name = Path.GetFileName(file);
            string extension = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
            RecoverySourceArtifactKind kind;
            string? format = null;
            if (extension == "OPF")
            {
                if (foundOpf || !await IsValidOpfAsync(
                        file, book, cancellationToken).ConfigureAwait(false))
                    issues.Add(Block("RECOVERY.CURRENT_METADATA_EXPORT_INVALID",
                        $"Current record {book.Id}",
                        "The current export must contain exactly one valid bounded OPF metadata file."));
                foundOpf = true;
                kind = RecoverySourceArtifactKind.OriginalMetadataOpf;
            }
            else if (string.Equals(name, "cover.jpg", StringComparison.OrdinalIgnoreCase))
            {
                Sha256Digest actual = await HashFileAsync(file, cancellationToken).ConfigureAwait(false);
                if (foundCover || !book.PublicationMetadata.HasCover
                    || new FileInfo(file).Length <= 0
                    || cover?.Fingerprint is { } expectedCover
                    && (expectedCover.SizeInBytes != new FileInfo(file).Length
                        || expectedCover.Sha256 != actual))
                    issues.Add(Block("RECOVERY.CURRENT_COVER_EXPORT_INVALID",
                        $"Current record {book.Id}",
                        "The exported cover is unexpected, duplicate, empty, or differs from the fresh scan."));
                foundCover = true;
                kind = RecoverySourceArtifactKind.OriginalCover;
            }
            else if (expectedFormats.TryGetValue(extension, out BookFormat? expected))
            {
                if (!foundFormats.Add(extension)
                    || expected.Fingerprint!.SizeInBytes != new FileInfo(file).Length
                    || expected.Fingerprint.Sha256
                    != await HashFileAsync(file, cancellationToken).ConfigureAwait(false))
                    issues.Add(Block("RECOVERY.CURRENT_EXPORTED_FORMAT_INVALID",
                        $"Current record {book.Id} / {extension}",
                        "The exported format is duplicate or differs from the fresh current-state hash."));
                kind = RecoverySourceArtifactKind.OtherManifestEntry;
                format = extension;
            }
            else
            {
                issues.Add(Block("RECOVERY.CURRENT_EXPORT_EXTRA_DATA", $"Current record {book.Id}",
                    "The current record export contains unmodeled extra data."));
                kind = RecoverySourceArtifactKind.OtherManifestEntry;
            }
            artifacts.Add(await DescribeAsync(bundle, file, kind, cancellationToken,
                logicalRecordId, book.Id, format).ConfigureAwait(false));
        }
        if (!foundOpf)
            issues.Add(Block("RECOVERY.CURRENT_METADATA_EXPORT_MISSING",
                $"Current record {book.Id}", "The Calibre metadata OPF export is missing."));
        if (book.PublicationMetadata.HasCover && !foundCover)
            issues.Add(Block("RECOVERY.CURRENT_COVER_EXPORT_MISSING",
                $"Current record {book.Id}", "The current cover could not be backed up."));
        foreach (string missing in expectedFormats.Keys.Where(value => !foundFormats.Contains(value)))
            issues.Add(Block("RECOVERY.CURRENT_EXPORTED_FORMAT_MISSING",
                $"Current record {book.Id} / {missing}",
                "The Calibre current-state export is missing an affected ebook format."));
    }

    private static string PreservationRole(
        RecoveryPlan plan,
        CalibreBookId recordId,
        string format)
    {
        ReconciledRecoveryRecord? record = plan.Definition.Reconciliation.Records.SingleOrDefault(
            value => (value.Identity.CurrentRecordId ?? value.Identity.OriginalRecordId) == recordId);
        return record?.Formats.Any(value => value.Format == format && value.PreserveCurrentContent) == true
            ? "preserve-unexpected-current-content"
            : "current-affected-state";
    }

    private static async Task CopySourceAuditArtifactsAsync(
        CreateRecoveryCurrentStateBackupRequest request,
        List<RecoveryCurrentStateBackupArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        (RecoverySourceArtifactKind Kind, string Name)[] required =
        [
            (RecoverySourceArtifactKind.CleanupPlan, "source-cleanup-plan.json"),
            (RecoverySourceArtifactKind.ExecutionJournal, "source-execution-journal.jsonl"),
            (RecoverySourceArtifactKind.OriginalBackupManifest, "source-backup-manifest.json"),
        ];
        string auditRoot = Path.Combine(request.BundleIdentity, "source-artifact-audit");
        Directory.CreateDirectory(auditRoot);
        foreach ((RecoverySourceArtifactKind kind, string name) in required)
        {
            VerifiedRecoverySourceArtifact[] candidates = request.Source.Artifacts
                .Where(value => value.Kind == kind)
                .GroupBy(value => value.PhysicalIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(value => value.First()).ToArray();
            if (candidates.Length != 1)
                throw new IOException($"The verified source {kind} artifact is not unique.");
            VerifiedRecoverySourceArtifact source = candidates[0];
            string destination = Path.Combine(auditRoot, name);
            await CopyAndVerifyAsync(source.PhysicalIdentity, destination,
                new(source.SizeInBytes, source.Sha256), cancellationToken).ConfigureAwait(false);
            artifacts.Add(await DescribeAsync(request.BundleIdentity, destination,
                kind, cancellationToken).ConfigureAwait(false));
        }
        VerifiedRecoverySourceArtifact[] summaries = request.Source.Artifacts
            .Where(value => value.Kind == RecoverySourceArtifactKind.ExecutionSummary)
            .GroupBy(value => value.PhysicalIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(value => value.First()).ToArray();
        if (summaries.Length > 1)
            throw new IOException("The verified source execution summary is not unique.");
        if (summaries.Length == 1)
        {
            VerifiedRecoverySourceArtifact summary = summaries[0];
            string destination = Path.Combine(auditRoot,
                "source-execution-summary.json");
            await CopyAndVerifyAsync(summary.PhysicalIdentity, destination,
                new(summary.SizeInBytes, summary.Sha256),
                cancellationToken).ConfigureAwait(false);
            artifacts.Add(await DescribeAsync(request.BundleIdentity, destination,
                RecoverySourceArtifactKind.ExecutionSummary,
                cancellationToken).ConfigureAwait(false));
        }
    }

    private static Sha256Digest ComputeInternalDigest(
        CreateRecoveryCurrentStateBackupRequest request,
        IEnumerable<RecoveryCurrentStateBackupArtifact> entries) =>
        ComputeInternalDigest(request.RecoveryExecutionId, request.Plan.Id,
            request.Plan.ContentDigest,
            request.CurrentState.Snapshot.Identity.CalibreLibraryUuid,
            request.CurrentState.FullFingerprint, entries);

    private static Sha256Digest ComputeInternalDigest(
        RecoveryExecutionId recoveryExecutionId,
        RecoveryPlanId recoveryPlanId,
        RecoveryPlanContentDigest recoveryPlanContentDigest,
        string libraryUuid,
        Sha256Digest sourceStateFingerprint,
        IEnumerable<RecoveryCurrentStateBackupArtifact> entries)
    {
        StringBuilder value = new();
        Add(value, ManifestSchema);
        Add(value, recoveryExecutionId.ToString());
        Add(value, recoveryPlanId.ToString());
        Add(value, recoveryPlanContentDigest.Value);
        Add(value, libraryUuid);
        Add(value, sourceStateFingerprint.Value);
        foreach (RecoveryCurrentStateBackupArtifact entry in entries)
        {
            Add(value, entry.RelativePath);
            Add(value, entry.Kind.ToString());
            Add(value, entry.SizeInBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(value, entry.Sha256.Value);
            Add(value, entry.LogicalRecordId?.Value ?? string.Empty);
            Add(value, entry.CurrentRecordId?.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            Add(value, entry.Format ?? string.Empty);
            Add(value, entry.PreservationRole ?? string.Empty);
        }
        return new(Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(value.ToString()))).ToLowerInvariant());
    }

    private static void Add(StringBuilder builder, string text) =>
        builder.Append(text.Length).Append(':').Append(text).Append(';');

    private static LogicalRecoveryRecordId LogicalRecordIdFor(
        RecoveryPlan plan,
        CalibreBookId currentRecordId) =>
        plan.Definition.Reconciliation.Records.Single(value =>
            value.Identity.CurrentRecordId == currentRecordId
            || value.Identity.OriginalRecordId == currentRecordId)
            .Identity.LogicalRecordId;

    private static async Task CopyAndVerifyAsync(
        string source,
        string destination,
        FormatFileFingerprint expected,
        CancellationToken cancellationToken)
    {
        await using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                         BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            if (input.Length != expected.SizeInBytes) throw new IOException("The source size changed.");
            await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        if (new FileInfo(destination).Length != expected.SizeInBytes
            || await HashFileAsync(destination, cancellationToken).ConfigureAwait(false) != expected.Sha256)
            throw new IOException("The copied backup did not verify.");
    }

    private static async Task<RecoveryCurrentStateBackupArtifact> DescribeAsync(
        string bundle,
        string path,
        RecoverySourceArtifactKind kind,
        CancellationToken cancellationToken,
        LogicalRecoveryRecordId? logicalRecordId = null,
        CalibreBookId? recordId = null,
        string? format = null,
        string? preservationRole = null)
    {
        FileInfo info = new(path);
        return new(Path.GetRelativePath(bundle, path).Replace('\\', '/'), kind,
            info.Length, await HashFileAsync(path, cancellationToken).ConfigureAwait(false),
            logicalRecordId, recordId, format, preservationRole);
    }

    private static async Task WriteJsonCreateNewAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumManifestBytes)
            throw new IOException("The manifest violates its size bound.");
        byte[] bytes = new byte[checked((int)info.Length)];
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static async Task<bool> IsValidOpfAsync(
        string path,
        CalibreBook expected,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumOpfBytes) return false;
        XmlReaderSettings settings = new()
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumOpfBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using XmlReader reader = XmlReader.Create(stream, settings);
        XDocument document = await XDocument.LoadAsync(
            reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        XElement? metadata = document.Descendants().SingleOrDefault(value =>
            value.Name.LocalName == "metadata");
        if (metadata is null) return false;

        string? title = ElementValues(metadata, "title").SingleOrDefault();
        XElement[] creators = metadata.Elements().Where(value =>
            value.Name.LocalName == "creator").ToArray();
        string[] authorNames = creators.Select(value => value.Value).ToArray();
        string[] authorSortNames = creators.Select(value =>
            AttributeValue(value, "file-as") ?? string.Empty).ToArray();
        string? authorSort = MetaValue(metadata, "calibre:author_sort");
        string? publisher = NormalizeOptional(ElementValues(metadata, "publisher").SingleOrDefault());
        DateTimeOffset? publicationDate = ParseDate(
            ElementValues(metadata, "date").SingleOrDefault());
        string? series = NormalizeOptional(MetaValue(metadata, "calibre:series"));
        decimal? seriesIndex = ParseDecimal(MetaValue(metadata, "calibre:series_index"));
        string[] languages = ElementValues(metadata, "language").ToArray();
        (string Type, string Value)[] identifiers = metadata.Elements()
            .Where(value => value.Name.LocalName == "identifier")
            .Select(value => (Type: (AttributeValue(value, "scheme") ?? string.Empty)
                    .Trim().ToLowerInvariant(),
                Value: value.Value.Trim()))
            .Where(value => value.Type.Length > 0
                && value.Type is not ("calibre" or "uuid"))
            .OrderBy(value => value.Type, StringComparer.Ordinal)
            .ThenBy(value => value.Value, StringComparer.Ordinal)
            .ToArray();
        (string Type, string Value)[] expectedIdentifiers = expected.Identifiers
            .Select(value => (Type: value.Type.Trim().ToLowerInvariant(),
                Value: value.Value.Trim()))
            .OrderBy(value => value.Type, StringComparer.Ordinal)
            .ThenBy(value => value.Value, StringComparer.Ordinal)
            .ToArray();

        return title == expected.Title
            && authorNames.SequenceEqual(expected.Authors.Select(value => value.Name))
            && authorSortNames.SequenceEqual(expected.Authors.Select(value => value.SortName))
            && authorSort == expected.AuthorSort
            && identifiers.SequenceEqual(expectedIdentifiers)
            && publisher == NormalizeOptional(expected.PublicationMetadata.Publisher)
            && publicationDate?.ToUniversalTime()
                == expected.PublicationMetadata.PublicationDate?.ToUniversalTime()
            && series == NormalizeOptional(expected.PublicationMetadata.Series)
            && seriesIndex == expected.PublicationMetadata.SeriesIndex
            && languages.SequenceEqual(expected.PublicationMetadata.Languages);
    }

    private static IEnumerable<string> ElementValues(XElement metadata, string localName) =>
        metadata.Elements().Where(value => value.Name.LocalName == localName)
            .Select(value => value.Value.Trim());

    private static string? AttributeValue(XElement element, string localName) =>
        element.Attributes().SingleOrDefault(value => value.Name.LocalName == localName)?.Value;

    private static string? MetaValue(XElement metadata, string name) =>
        metadata.Elements().Where(value => value.Name.LocalName == "meta")
            .SingleOrDefault(value => string.Equals(
                AttributeValue(value, "name"), name, StringComparison.OrdinalIgnoreCase)) is { } meta
            ? NormalizeOptional(AttributeValue(meta, "content") ?? meta.Value)
            : null;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset? ParseDate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
            : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed)
                ? parsed
                : DateTimeOffset.MinValue;

    private static decimal? ParseDecimal(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
            : decimal.TryParse(value, NumberStyles.Number,
                CultureInfo.InvariantCulture, out decimal parsed)
                ? parsed
                : decimal.MinValue;

    private static async Task<Sha256Digest> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new(Convert.ToHexString(await SHA256.HashDataAsync(
            stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant());
    }

    private static ManifestEntryDto ToDto(RecoveryCurrentStateBackupArtifact entry) =>
        new(entry.RelativePath, entry.Kind, entry.SizeInBytes, entry.Sha256.Value,
            entry.LogicalRecordId?.Value, entry.CurrentRecordId?.Value,
            entry.Format, entry.PreservationRole);

    private static RecoveryCurrentStateBackupArtifact FromDto(ManifestEntryDto entry) =>
        new(entry.RelativePath, entry.Kind, entry.SizeInBytes, new(entry.Sha256),
            entry.LogicalRecordId is null ? null : new(entry.LogicalRecordId),
            entry.CurrentRecordId is null ? null : new(entry.CurrentRecordId.Value),
            entry.Format, entry.PreservationRole);

    private static RecoveryIssue ConvertIssue(ExecutionIssue issue) =>
        new($"RECOVERY.{issue.Code}",
            issue.Severity == ExecutionIssueSeverity.BlockingError
                ? RecoveryIssueSeverity.Blocking : RecoveryIssueSeverity.Information,
            "Current-state backup destination", issue.Explanation,
            null, issue.RecordId, issue.Format);

    private static RecoveryIssue Block(string code, string subject, string explanation) =>
        new(code, RecoveryIssueSeverity.Blocking, subject, explanation);

    private static RecoveryIssue[] Ordered(IEnumerable<RecoveryIssue> issues) =>
        issues.OrderBy(value => value.Severity).ThenBy(value => value.Code, StringComparer.Ordinal)
            .ThenBy(value => value.Subject, StringComparer.Ordinal).ToArray();

    private sealed record SourceChainDto(
        string RecoveryPlanId,
        string RecoveryPlanContentDigest,
        string SourceCleanupPlanId,
        string SourceCleanupPlanContentDigest,
        string SourceCleanupExecutionId,
        string OriginalBundleIdentity,
        string SourceJournalFileDigest,
        string SourceJournalFinalEntryHash,
        string? SourceTerminalSummaryDigest,
        string OriginalManifestInternalDigest,
        string OriginalManifestFileDigest);

    private sealed record ConfirmationDto(
        string RecoveryPlanId,
        int RecoveryPlanRevision,
        string RecoveryPlanContentDigest,
        string SourceCleanupExecutionId,
        string LibraryUuid,
        string CanonicalRootIdentityDigest,
        string CurrentStateFingerprint,
        string CapabilityProfile,
        string CurrentStateBackupDestinationIdentity,
        string DestructiveOperationDigest,
        bool OtherMutatorsClosed,
        bool SafeBoundaryCancellationUnderstood);

    private sealed record InventoryRecordDto(
        long RecordId,
        string Title,
        string AuthorSort,
        InventoryAuthorDto[] Authors,
        InventoryIdentifierDto[] Identifiers,
        string? Publisher,
        DateTimeOffset? PublicationDate,
        string? Series,
        decimal? SeriesIndex,
        string[] Languages,
        bool HasCover,
        string RelativeDirectory,
        InventoryFormatDto[] Formats);

    private sealed record InventoryAuthorDto(long AuthorId, string Name, string SortName);

    private sealed record InventoryIdentifierDto(string Type, string Value);

    private sealed record InventoryFormatDto(
        string Format,
        string StoredFileName,
        string RelativePath,
        FormatFileStatus Status,
        long? SizeInBytes,
        string? Sha256,
        long? ObservedLength,
        DateTimeOffset? CreationTimeUtc,
        DateTimeOffset? LastWriteTimeUtc,
        int? Attributes);

    private sealed record ManifestDto(
        string SchemaVersion,
        string RecoveryExecutionId,
        string RecoveryPlanId,
        string RecoveryPlanContentDigest,
        string LibraryUuid,
        string SourceStateFingerprint,
        DateTimeOffset CreatedAtUtc,
        string ManifestInternalDigest,
        ManifestEntryDto[] Entries);

    private sealed record ManifestEntryDto(
        string RelativePath,
        RecoverySourceArtifactKind Kind,
        long SizeInBytes,
        string Sha256,
        string? LogicalRecordId,
        long? CurrentRecordId,
        string? Format,
        string? PreservationRole);
}
