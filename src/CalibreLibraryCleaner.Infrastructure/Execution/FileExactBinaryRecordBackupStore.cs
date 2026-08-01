using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Infrastructure.Execution;

internal sealed class FileExactBinaryRecordBackupStore(IClock clock) : IExactBinaryRecordBackupStore
{
    private const int BufferSize = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public async Task<ExactBinaryRecordBackupInputs> CreateInputsAsync(
        ExecutionWorkspace workspace,
        ExactBinaryCleanupPlan plan,
        string libraryRoot,
        CalibreToolDescriptor tool,
        string applicationVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(plan);
        List<ExecutionIssue> issues = [];
        Dictionary<CalibreBookId, string> exportDirectories = [];
        try
        {
            await WriteCreateNewAsync(Path.Combine(workspace.BundlePath, "approved.exact-binary-plan.json"),
                JsonSerializer.SerializeToUtf8Bytes(plan, JsonOptions), cancellationToken).ConfigureAwait(false);
            await WriteCreateNewAsync(Path.Combine(workspace.BundlePath, "tool-identity.json"),
                JsonSerializer.SerializeToUtf8Bytes(new
                {
                    tool.CanonicalExecutablePath,
                    tool.Identity.ProductVersion,
                    executableSha256 = tool.Identity.ExecutableSha256.Value,
                    tool.Identity.CapabilityProfile,
                }, JsonOptions), cancellationToken).ConfigureAwait(false);
            await WriteCreateNewAsync(Path.Combine(workspace.BundlePath, "application-identity.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { applicationVersion }, JsonOptions), cancellationToken).ConfigureAwait(false);

            string rawRoot = Path.Combine(workspace.BundlePath, "raw-formats");
            string exportRoot = Path.Combine(workspace.BundlePath, "exports");
            Directory.CreateDirectory(rawRoot);
            Directory.CreateDirectory(exportRoot);
            foreach (ExpectedRecordState record in plan.Definition.ExpectedRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string recordName = record.RecordId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string rawDirectory = Path.Combine(rawRoot, recordName);
                string exportDirectory = Path.Combine(exportRoot, recordName);
                Directory.CreateDirectory(rawDirectory);
                Directory.CreateDirectory(exportDirectory);
                exportDirectories.Add(record.RecordId, exportDirectory);
                foreach (ExpectedFormatState format in record.Formats)
                {
                    if (!ExecutionPathGuard.TryValidateContainedRegularFile(libraryRoot, format.RelativePath,
                            out string? source, out string? reason))
                    {
                        issues.Add(Block("BINARY_BACKUP.SOURCE_UNSAFE", reason ?? "A managed source path is unsafe.",
                            record.RecordId, format.Format));
                        continue;
                    }
                    string destination = Path.Combine(rawDirectory, $"book.{format.Format.ToLowerInvariant()}");
                    ExecutionIssue? copyIssue = await CopyExpectedAsync(source!, destination, format, cancellationToken)
                        .ConfigureAwait(false);
                    if (copyIssue is not null) issues.Add(copyIssue);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            issues.Add(Block("BINARY_BACKUP.CREATION_FAILED", "The exact-record backup inputs could not be created completely."));
        }
        return new(workspace, exportDirectories, issues);
    }

    public async Task<ExactBinaryRecordBackupResult> VerifyAndSealAsync(
        ExactBinaryRecordBackupInputs inputs,
        ExactBinaryCleanupPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(plan);
        List<ExecutionIssue> issues = [];
        try
        {
            foreach (ExpectedRecordState record in plan.Definition.ExpectedRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!inputs.ExportDirectories.TryGetValue(record.RecordId, out string? exportDirectory)
                    || !Directory.Exists(exportDirectory))
                {
                    issues.Add(Block("BINARY_BACKUP.EXPORT_MISSING", "An involved record export is missing.", record.RecordId));
                    continue;
                }
                string[] files = Directory.EnumerateFiles(exportDirectory, "*", SearchOption.AllDirectories).ToArray();
                if (!files.Any(value => string.Equals(Path.GetExtension(value), ".opf", StringComparison.OrdinalIgnoreCase)))
                    issues.Add(Block("BINARY_BACKUP.METADATA_MISSING", "An involved record export has no OPF metadata.", record.RecordId));
                if (record.HasCover && !files.Any(value => string.Equals(Path.GetFileName(value), "cover.jpg", StringComparison.OrdinalIgnoreCase)))
                    issues.Add(Block("BINARY_BACKUP.COVER_MISSING", "An involved record export has no reported cover.", record.RecordId));
                foreach (ExpectedFormatState format in record.Formats)
                {
                    string rawPath = Path.Combine(inputs.Workspace.BundlePath, "raw-formats",
                        record.RecordId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        $"book.{format.Format.ToLowerInvariant()}");
                    if (!await FileMatchesAsync(rawPath, format.Fingerprint, cancellationToken).ConfigureAwait(false))
                        issues.Add(Block("BINARY_BACKUP.RAW_FORMAT_INVALID", "A raw format backup is missing or changed.",
                            record.RecordId, format.Format));
                    string[] exportedCandidates = files.Where(value =>
                        string.Equals(Path.GetExtension(value), $".{format.Format}", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (exportedCandidates.Length != 1
                        || !await FileMatchesAsync(exportedCandidates[0], format.Fingerprint, cancellationToken).ConfigureAwait(false))
                        issues.Add(Block("BINARY_BACKUP.EXPORTED_FORMAT_INVALID",
                            "The Calibre export does not contain exactly one matching format.", record.RecordId, format.Format));
                }
            }
            if (issues.Count > 0) return new(null, issues);

            string[] artifactPaths = Directory.EnumerateFiles(inputs.Workspace.BundlePath, "*", SearchOption.AllDirectories)
                .Where(value => !string.Equals(Path.GetFileName(value), "execution.audit.jsonl", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFileName(value), "backup-manifest.json", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .ToArray();
            List<ExactBinaryRecordBackupEntry> entries = [];
            foreach (string path in artifactPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
                {
                    issues.Add(Block("BINARY_BACKUP.ARTIFACT_UNSAFE", "A backup artifact is linked or unsafe."));
                    continue;
                }
                FileInfo info = new(path);
                entries.Add(new(Path.GetRelativePath(inputs.Workspace.BundlePath, path).Replace('\\', '/'),
                    info.Length, await HashFileAsync(path, cancellationToken).ConfigureAwait(false)));
            }
            if (issues.Count > 0) return new(null, issues);
            Sha256Digest manifestDigest = ComputeManifestDigest(plan, entries);
            ExactBinaryRecordBackupManifest manifest = new(inputs.Workspace.ExecutionId, plan.Id, plan.ContentDigest,
                clock.GetUtcNow(), entries.AsReadOnly(), manifestDigest);
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            string manifestPath = Path.Combine(inputs.Workspace.BundlePath, "backup-manifest.json");
            await WriteCreateNewAsync(manifestPath, manifestBytes, cancellationToken).ConfigureAwait(false);
            using JsonDocument restored = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false));
            if (restored.RootElement.GetProperty("manifestDigest").GetProperty("value").GetString() != manifestDigest.Value)
                issues.Add(Block("BINARY_BACKUP.MANIFEST_REREAD_FAILED", "The backup manifest did not survive durable reread."));
            issues.AddRange(await VerifyAvailableAsync(inputs.Workspace, manifest, cancellationToken).ConfigureAwait(false));
            return issues.Count == 0 ? new(manifest, issues) : new(null, issues);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            issues.Add(Block("BINARY_BACKUP.VERIFICATION_FAILED", "The exact-record backup could not be verified and sealed."));
            return new(null, issues);
        }
    }

    public async Task<IReadOnlyList<ExecutionIssue>> VerifyAvailableAsync(
        ExecutionWorkspace workspace,
        ExactBinaryRecordBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        List<ExecutionIssue> issues = [];
        try
        {
            string manifestPath = Path.Combine(workspace.BundlePath, "backup-manifest.json");
            if (!File.Exists(manifestPath) || !ExecutionPathGuard.TryRejectReparsePoints(manifestPath, true, out _))
                issues.Add(Block("BINARY_BACKUP.MANIFEST_UNAVAILABLE", "The sealed backup manifest is unavailable."));
            foreach (ExactBinaryRecordBackupEntry entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.GetFullPath(Path.Combine(workspace.BundlePath,
                    entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!ExecutionPathGuard.IsContained(workspace.BundlePath, path)
                    || !File.Exists(path)
                    || !ExecutionPathGuard.TryRejectReparsePoints(path, true, out _)
                    || new FileInfo(path).Length != entry.SizeInBytes
                    || await HashFileAsync(path, cancellationToken).ConfigureAwait(false) != entry.Sha256)
                    issues.Add(Block("BINARY_BACKUP.ARTIFACT_CHANGED", "A verified backup artifact is missing or changed."));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            issues.Add(Block("BINARY_BACKUP.AVAILABILITY_UNKNOWN", "The verified backup bundle could not be rechecked."));
        }
        return issues;
    }

    public async Task AppendAuditAsync(
        ExecutionWorkspace workspace,
        ExactBinaryRecordAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(auditEvent, JsonOptions);
        string path = Path.Combine(workspace.BundlePath, "execution.audit.jsonl");
        await using FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<ExecutionIssue?> CopyExpectedAsync(
        string source,
        string destination,
        ExpectedFormatState expected,
        CancellationToken cancellationToken)
    {
        try
        {
            FileInfo before = new(source);
            if (!ObservationMatches(before, expected))
                return Block("BINARY_BACKUP.SOURCE_CHANGED", "A managed source changed before backup.", expected.RecordId, expected.Format);
            await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, BufferSize, cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            await output.DisposeAsync().ConfigureAwait(false);
            FileInfo after = new(source); after.Refresh();
            if (!ObservationMatches(after, expected)
                || !await FileMatchesAsync(destination, expected.Fingerprint, cancellationToken).ConfigureAwait(false))
                return Block("BINARY_BACKUP.SOURCE_CHANGED", "A managed source changed while it was backed up.", expected.RecordId, expected.Format);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Block("BINARY_BACKUP.COPY_FAILED", "A managed format could not be copied and verified.", expected.RecordId, expected.Format);
        }
    }

    private static bool ObservationMatches(FileInfo info, ExpectedFormatState expected) =>
        info.Exists && info.Length == expected.Observation.Length
        && new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero) == expected.Observation.CreationTimeUtc
        && new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) == expected.Observation.LastWriteTimeUtc
        && (int)info.Attributes == expected.Observation.Attributes;

    private static async Task<bool> FileMatchesAsync(
        string path,
        FormatFileFingerprint fingerprint,
        CancellationToken cancellationToken) =>
        File.Exists(path) && new FileInfo(path).Length == fingerprint.SizeInBytes
        && await HashFileAsync(path, cancellationToken).ConfigureAwait(false) == fingerprint.Sha256;

    private static async Task<Sha256Digest> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant());
    }

    private static Sha256Digest ComputeManifestDigest(
        ExactBinaryCleanupPlan plan,
        IEnumerable<ExactBinaryRecordBackupEntry> entries)
    {
        StringBuilder canonical = new($"{plan.Id}|{plan.ContentDigest.Value}|");
        foreach (ExactBinaryRecordBackupEntry entry in entries)
            canonical.Append(entry.RelativePath).Append('|').Append(entry.SizeInBytes).Append('|').Append(entry.Sha256.Value).Append('|');
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant());
    }

    private static async Task WriteCreateNewAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static ExecutionIssue Block(
        string code,
        string explanation,
        CalibreBookId? recordId = null,
        string? format = null) => new(code, ExecutionIssueSeverity.BlockingError, explanation, recordId, format);
}
