using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Infrastructure.Execution;
using CalibreLibraryCleaner.Infrastructure.Plans;

namespace CalibreLibraryCleaner.Infrastructure.Recovery;

internal sealed class FileRecoverySourceArtifactReader(
    IExecutionBackupStore backupStore) : IRecoverySourceArtifactReader
{
    private const string JournalSchema = "cleanup-execution-journal/1.0";
    private const long MaximumArtifactBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };
    private static readonly HashSet<string> KnownEvents = new(StringComparer.Ordinal)
    {
        "JournalHeader", "ExecutionCreated", "PreflightStarted", "PreflightVerified",
        "BackupStarted", "BackupVerified", "FinalMutationGate", "MutationStarting",
        "PreflightFailed", "BackupFailed", "FinalMutationGateFailed",
        "OperationStarting", "CommandFinished", "OperationVerified", "OperationSatisfiedNoOp",
        "VerificationPassed", "VerificationFailed", "DestructiveGateApproved",
        "DestructiveGateDeclined", "CancellationRequested", "FailureStateScan",
        "FailureStateScanFailed", "ExecutionStopped", "TerminalSummary",
    };

    public async Task<RecoverySourceInspection> ReadAndVerifyAsync(
        string sourceBundle,
        CancellationToken cancellationToken)
    {
        List<RecoveryIssue> issues = [];
        List<VerifiedRecoverySourceArtifact> artifacts = [];
        CleanupPlan? plan = null;
        VerifiedBackupManifest? manifest = null;
        VerifiedExecutionJournalProjection? journal = null;
        ExecutionHistoryEntry? summary = null;
        ExecutionToolIdentity? tool = null;
        string? applicationVersion = null;
        Sha256Digest? manifestFileDigest = null;
        string bundle;
        try
        {
            bundle = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceBundle));
            if (!Directory.Exists(bundle)
                || !ExecutionPathGuard.TryRejectReparsePoints(bundle, true, out _))
                return Failed(sourceBundle, Block("RECOVERY.SOURCE_BUNDLE_UNSAFE",
                    "The Milestone 7 execution bundle is missing, linked, or unreadable."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                   or ArgumentException or NotSupportedException)
        {
            return Failed(sourceBundle, Block("RECOVERY.SOURCE_BUNDLE_UNSAFE",
                "The Milestone 7 execution bundle cannot be canonicalized safely."));
        }

        try
        {
            string planPath = Path.Combine(bundle, "approved.cleanup-plan.json");
            byte[] planBytes = await ReadBoundedAsync(planPath, cancellationToken).ConfigureAwait(false);
            CleanupPlanStoreReadResult planResult = CleanupPlanJsonSerializer.Deserialize(planBytes);
            if (!planResult.IsSuccess)
                issues.Add(Block("RECOVERY.SOURCE_PLAN_INVALID",
                    "The original cleanup plan is missing, malformed, unsupported, or tampered."));
            else
            {
                plan = planResult.Plan;
                artifacts.Add(await ArtifactAsync(bundle, planPath,
                    RecoverySourceArtifactKind.CleanupPlan, cancellationToken).ConfigureAwait(false));
            }

            string manifestPath = Path.Combine(bundle, "backup-manifest.json");
            manifest = await FileExecutionBackupStore.ReadManifestAsync(
                manifestPath, cancellationToken).ConfigureAwait(false);
            manifestFileDigest = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            artifacts.Add(await ArtifactAsync(bundle, manifestPath,
                RecoverySourceArtifactKind.OriginalBackupManifest, cancellationToken).ConfigureAwait(false));

            ExecutionWorkspace workspace = new(manifest.ExecutionId, bundle,
                Path.GetDirectoryName(bundle) ?? bundle);
            foreach (ExecutionIssue originalIssue in await backupStore.VerifyAvailableAsync(
                         workspace, manifest, cancellationToken).ConfigureAwait(false))
                issues.Add(ConvertIssue(originalIssue, "Original backup"));
            foreach (BackupManifestEntry entry in manifest.Entries)
            {
                string physical = ResolveManifestEntry(bundle, entry.RelativePath);
                artifacts.Add(new(entry.RelativePath, physical, MapKind(entry.Kind),
                    entry.SizeInBytes, entry.Sha256, entry.RecordId, entry.Format));
            }

            string journalPath = Path.Combine(bundle, "execution.journal.jsonl");
            journal = await ReadJournalAsync(journalPath, plan, cancellationToken).ConfigureAwait(false);
            artifacts.Add(await ArtifactAsync(bundle, journalPath,
                RecoverySourceArtifactKind.ExecutionJournal, cancellationToken).ConfigureAwait(false));

            string summaryPath = Path.Combine(bundle, "execution-summary.json");
            bool journalClaimsTerminal = journal.DurableEventKinds.Count > 0
                && journal.DurableEventKinds[^1] == "TerminalSummary";
            if (journalClaimsTerminal)
            {
                byte[] summaryBytes = await ReadBoundedAsync(summaryPath, cancellationToken)
                    .ConfigureAwait(false);
                summary = JsonSerializer.Deserialize<ExecutionHistoryEntry>(summaryBytes, Options)
                    ?? throw new JsonException("The terminal summary is empty.");
                journal = journal with
                {
                    TerminalSummaryDigest = new(Convert.ToHexString(
                        SHA256.HashData(summaryBytes)).ToLowerInvariant()),
                };
                artifacts.Add(await ArtifactAsync(bundle, summaryPath,
                    RecoverySourceArtifactKind.ExecutionSummary, cancellationToken).ConfigureAwait(false));
            }
            else if (File.Exists(summaryPath))
            {
                _ = await ReadBoundedAsync(summaryPath, cancellationToken)
                    .ConfigureAwait(false);
                artifacts.Add(await ArtifactAsync(bundle, summaryPath,
                    RecoverySourceArtifactKind.ExecutionSummary,
                    cancellationToken).ConfigureAwait(false));
                issues.Add(new("RECOVERY.SOURCE_ORPHAN_SUMMARY_IGNORED",
                    RecoveryIssueSeverity.Information,
                    "Original recovery source",
                    "A summary without a terminal journal event was retained as audit evidence but was not treated as authoritative."));
            }

            ToolIdentityDto toolDto = JsonSerializer.Deserialize<ToolIdentityDto>(
                await ReadBoundedAsync(Path.Combine(bundle, "tool-identity.json"), cancellationToken)
                    .ConfigureAwait(false), Options)
                ?? throw new JsonException("The tool identity is empty.");
            tool = new(toolDto.CanonicalExecutablePath, toolDto.ProductVersion,
                new(toolDto.ExecutableSha256), toolDto.CapabilityProfile);
            ApplicationIdentityDto application = JsonSerializer.Deserialize<ApplicationIdentityDto>(
                await ReadBoundedAsync(Path.Combine(bundle, "application-identity.json"), cancellationToken)
                    .ConfigureAwait(false), Options)
                ?? throw new JsonException("The application identity is empty.");
            applicationVersion = application.ApplicationVersion;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                   or JsonException or ArgumentException or InvalidOperationException
                   or NotSupportedException or CryptographicException)
        {
            issues.Add(Block("RECOVERY.SOURCE_ARTIFACT_INVALID",
                "A required original plan, manifest, journal, summary, or identity artifact is absent or invalid."));
        }

        if (plan is not null && manifest is not null && journal is not null)
        {
            if (manifest.PlanId != plan.Id || manifest.PlanContentDigest != plan.ContentDigest
                || manifest.LibraryUuid != plan.InputIdentity.LibraryUuid
                || journal.PlanId != plan.Id || journal.PlanContentDigest != plan.ContentDigest
                || journal.ExecutionId != manifest.ExecutionId
                || journal.LibraryUuid != manifest.LibraryUuid)
                issues.Add(Block("RECOVERY.SOURCE_CROSSLINK_MISMATCH",
                    "The original plan, manifest, and journal do not identify the same plan, execution, and library."));
        }
        if (journal is not null
            && journal.DurableEventKinds.Count > 0
            && journal.DurableEventKinds[^1] == "TerminalSummary"
            && summary is null)
            issues.Add(Block("RECOVERY.SOURCE_SUMMARY_MISSING",
                "The source journal claims a terminal state without its immutable summary."));
        if (summary is not null && journal is not null
            && (summary.ExecutionId != journal.ExecutionId
                || summary.PlanId != journal.PlanId
                || summary.PlanContentDigest != journal.PlanContentDigest
                || summary.LibraryUuid != journal.LibraryUuid
                || summary.State != journal.LastState
                || summary.Disposition != journal.LastDisposition
                || summary.MutationStarted != journal.MutationStarted))
            issues.Add(Block("RECOVERY.SOURCE_SUMMARY_MISMATCH",
                "The terminal summary contradicts the durable execution journal."));
        if (journal is { HasKnownDurableState: false })
            issues.Add(Block("RECOVERY.SOURCE_JOURNAL_UNKNOWN",
                "The source journal has an unknown or contradictory durable state."));

        return new(bundle, plan, manifest, journal, summary, tool, applicationVersion,
            manifestFileDigest, artifacts, Ordered(issues));
    }

    private static async Task<VerifiedExecutionJournalProjection> ReadJournalAsync(
        string path,
        CleanupPlan? plan,
        CancellationToken cancellationToken)
    {
        string previousHash = new('0', 64);
        List<JournalLineDto> lines = [];
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(stream, Encoding.UTF8, false, 16 * 1024);
        int sequence = 1;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } text)
        {
            if (text.Length is <= 0 or > 1_000_000 || lines.Count >= 100_000)
                throw new JsonException("The journal violates its bounds.");
            JournalLineDto line = JsonSerializer.Deserialize<JournalLineDto>(text, Options)
                ?? throw new JsonException("The journal line is empty.");
            if (line.Schema != JournalSchema || line.Sequence != sequence
                || line.PreviousHash != previousHash
                || !KnownEvents.Contains(line.Event.Kind)
                || ComputeEntryHash(line with { EntryHash = string.Empty }) != line.EntryHash)
                throw new JsonException("The journal hash chain or event kind is invalid.");
            if (lines.Count > 0)
            {
                JournalLineDto prior = lines[^1];
                if (prior.Event.Kind == "TerminalSummary"
                    || line.ExecutionId != prior.ExecutionId || line.PlanId != prior.PlanId
                    || line.PlanContentDigest != prior.PlanContentDigest
                    || line.LibraryUuid != prior.LibraryUuid
                    || line.ApplicationVersion != prior.ApplicationVersion
                    || line.Event.State != prior.Event.State
                    && !CleanupExecution.IsLegal(prior.Event.State, line.Event.State))
                    throw new JsonException("The journal contains a contradictory durable transition.");
            }
            lines.Add(line);
            previousHash = line.EntryHash;
            sequence++;
        }
        if (lines.Count == 0 || lines[0].Event.Kind != "JournalHeader")
            throw new JsonException("The journal header is absent.");
        JournalLineDto last = lines[^1];
        CleanupExecutionCapabilityResult graphResult = plan is null
            ? new(null, [])
            : CleanupExecutionCapabilityPolicy.Evaluate(plan);
        if (!graphResult.IsSupported)
            throw new JsonException("The source execution graph cannot be reconstructed.");

        Dictionary<ExecutionOperationId, DurableSourceOperationState> states =
            graphResult.Graph!.Operations.ToDictionary(
                operation => operation.Id, _ => DurableSourceOperationState.NotStarted);
        Dictionary<ExecutionOperationId, SourceOperationJournalStage> stages =
            graphResult.Graph.Operations.ToDictionary(
                operation => operation.Id, _ => SourceOperationJournalStage.NotStarted);
        Dictionary<ExecutionOperationId, CleanupExecutionOperation> operations =
            graphResult.Graph.Operations.ToDictionary(operation => operation.Id);
        bool verifiedBackup = false;
        bool mutationBoundary = false;
        bool terminal = false;
        bool finalVerificationPassed = false;
        foreach (JournalLineDto line in lines)
        {
            if (terminal)
                throw new JsonException("The source journal continues after its terminal summary.");
            if (line.Event.MutationStarted && !verifiedBackup)
                throw new JsonException("The source journal marks mutation before backup verification.");
            switch (line.Event.Kind)
            {
                case "BackupVerified":
                    if (mutationBoundary)
                        throw new JsonException("Backup verification follows the mutation boundary.");
                    verifiedBackup = true;
                    break;
                case "MutationStarting":
                    if (!verifiedBackup || mutationBoundary)
                        throw new JsonException("The mutation boundary is absent, duplicated, or precedes backup verification.");
                    mutationBoundary = true;
                    break;
                case "VerificationPassed"
                    when line.Event.OperationId is null
                         && line.Event.State == CleanupExecutionState.Verifying:
                    finalVerificationPassed = true;
                    break;
                case "TerminalSummary":
                    if (line.Event.State == CleanupExecutionState.Completed
                        && (!finalVerificationPassed
                            || stages.Values.Any(value => value is not (
                                SourceOperationJournalStage.Verified
                                or SourceOperationJournalStage.SatisfiedNoOp))))
                        throw new JsonException("Completed source recovery lacks durable final verification.");
                    terminal = true;
                    break;
            }

            ExecutionOperationId? id = line.Event.OperationId;
            bool operationEvent = line.Event.Kind is "OperationStarting" or "CommandFinished"
                or "OperationVerified" or "OperationSatisfiedNoOp"
                || line.Event.Kind is "VerificationPassed" or "VerificationFailed"
                && id is not null;
            if (id is null)
            {
                if (line.Event.Kind is "OperationStarting" or "CommandFinished"
                    or "OperationVerified" or "OperationSatisfiedNoOp")
                    throw new JsonException("An operation journal event lacks its operation ID.");
                continue;
            }
            if (!operations.TryGetValue(id, out CleanupExecutionOperation? operation)
                || !states.TryGetValue(id, out _))
                throw new JsonException("The journal refers to an operation outside the source plan.");
            if (!operationEvent) continue;
            bool dependenciesSatisfied = operation.DependencyIds.All(dependency =>
                stages[dependency] is SourceOperationJournalStage.Verified
                    or SourceOperationJournalStage.SatisfiedNoOp);
            SourceOperationJournalStage priorStage = stages[id];
            SourceOperationJournalStage nextStage = line.Event.Kind switch
            {
                "OperationSatisfiedNoOp"
                    when priorStage == SourceOperationJournalStage.NotStarted
                         && dependenciesSatisfied =>
                    SourceOperationJournalStage.SatisfiedNoOp,
                "OperationStarting"
                    when priorStage == SourceOperationJournalStage.NotStarted
                         && dependenciesSatisfied && mutationBoundary =>
                    SourceOperationJournalStage.Started,
                "CommandFinished"
                    when priorStage == SourceOperationJournalStage.Started
                         && line.Event.ExitCode == 0 && line.Event.FailureCode is null =>
                    SourceOperationJournalStage.CommandSucceeded,
                "CommandFinished"
                    when priorStage == SourceOperationJournalStage.Started =>
                    SourceOperationJournalStage.CommandFailed,
                "VerificationPassed"
                    when priorStage == SourceOperationJournalStage.CommandSucceeded =>
                    SourceOperationJournalStage.SemanticallyVerified,
                "VerificationFailed"
                    when priorStage == SourceOperationJournalStage.CommandSucceeded =>
                    SourceOperationJournalStage.VerificationFailed,
                "OperationVerified"
                    when priorStage == SourceOperationJournalStage.SemanticallyVerified =>
                    SourceOperationJournalStage.Verified,
                _ => throw new JsonException(
                    "The source journal contains an illegal per-operation event sequence."),
            };
            stages[id] = nextStage;
            states[id] = nextStage switch
            {
                SourceOperationJournalStage.SatisfiedNoOp =>
                    DurableSourceOperationState.SatisfiedNoOp,
                SourceOperationJournalStage.Verified =>
                    DurableSourceOperationState.DurablyCompleted,
                SourceOperationJournalStage.CommandFailed =>
                    DurableSourceOperationState.FailedBeforeCompletion,
                SourceOperationJournalStage.Started
                    or SourceOperationJournalStage.CommandSucceeded
                    or SourceOperationJournalStage.SemanticallyVerified
                    or SourceOperationJournalStage.VerificationFailed =>
                    DurableSourceOperationState.CommandOutcomeUncertain,
                _ => DurableSourceOperationState.NotStarted,
            };
        }
        SourceOperationProjection[] projections = graphResult.Graph.Operations
            .Select(operation => new SourceOperationProjection(
                operation.Id, operation.Kind, states[operation.Id],
                operation.TargetRecordId, operation.SourceRecordId,
                operation.Format, operation.DependencyIds))
            .ToArray();
        bool mutationStarted = lines.Any(line =>
            line.Event.MutationStarted || line.Event.Kind == "MutationStarting");
        bool known = lines.All(line => KnownEvents.Contains(line.Event.Kind))
            && (!mutationStarted || verifiedBackup)
            && projections.All(value =>
                value.State != DurableSourceOperationState.Contradictory);
        return new(JournalSchema, new(Guid.Parse(last.ExecutionId)), new(Guid.Parse(last.PlanId)),
            new(last.PlanContentDigest), last.LibraryUuid, last.ApplicationVersion,
            await HashFileAsync(path, cancellationToken).ConfigureAwait(false),
            last.EntryHash, null, last.Event.State,
            Disposition(last.Event.State, mutationStarted), mutationStarted,
            verifiedBackup, projections,
            lines.Select(line => line.Event.Kind).ToArray(), known);
    }

    private static CleanupExecutionDisposition Disposition(
        CleanupExecutionState state,
        bool mutationStarted) => state switch
        {
            CleanupExecutionState.Completed => CleanupExecutionDisposition.Completed,
            CleanupExecutionState.CancelledBeforeMutation => CleanupExecutionDisposition.CancelledBeforeMutation,
            CleanupExecutionState.PreflightFailed or CleanupExecutionState.BackupFailed
                or CleanupExecutionState.ExecutionFailedBeforeMutation => CleanupExecutionDisposition.Failed,
            CleanupExecutionState.ExecutionPartiallyApplied => CleanupExecutionDisposition.PartiallyApplied,
            CleanupExecutionState.VerificationFailed => CleanupExecutionDisposition.VerificationFailed,
            CleanupExecutionState.RecoveryRequired => CleanupExecutionDisposition.RecoveryRequired,
            _ => mutationStarted
                ? CleanupExecutionDisposition.RecoveryRequired
                : CleanupExecutionDisposition.InProgress,
        };

    private static string ComputeEntryHash(JournalLineDto line)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
            line with { EntryHash = string.Empty }, Options);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static string ResolveManifestEntry(string bundle, string relative)
    {
        string physical = Path.GetFullPath(Path.Combine(bundle,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!ExecutionPathGuard.IsContained(bundle, physical)
            || !File.Exists(physical)
            || !ExecutionPathGuard.TryRejectReparsePoints(physical, true, out _))
            throw new IOException("A manifest entry is outside the source bundle.");
        return physical;
    }

    private static RecoverySourceArtifactKind MapKind(BackupArtifactKind kind) => kind switch
    {
        BackupArtifactKind.CleanupPlan => RecoverySourceArtifactKind.CleanupPlan,
        BackupArtifactKind.RawFormat => RecoverySourceArtifactKind.OriginalRawFormat,
        BackupArtifactKind.RecordMetadataOpf => RecoverySourceArtifactKind.OriginalMetadataOpf,
        BackupArtifactKind.Cover => RecoverySourceArtifactKind.OriginalCover,
        BackupArtifactKind.ToolIdentity => RecoverySourceArtifactKind.ToolIdentity,
        BackupArtifactKind.ApplicationIdentity => RecoverySourceArtifactKind.ApplicationIdentity,
        BackupArtifactKind.ManagedState => RecoverySourceArtifactKind.ManagedState,
        _ => RecoverySourceArtifactKind.OtherManifestEntry,
    };

    private static async Task<VerifiedRecoverySourceArtifact> ArtifactAsync(
        string bundle,
        string path,
        RecoverySourceArtifactKind kind,
        CancellationToken cancellationToken)
    {
        FileInfo info = new(path);
        return new(Path.GetRelativePath(bundle, path).Replace('\\', '/'), path, kind,
            info.Length, await HashFileAsync(path, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || !ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
            throw new IOException("A source artifact is absent or linked.");
        FileInfo info = new(path);
        if (info.Length is <= 0 or > MaximumArtifactBytes)
            throw new IOException("A source artifact violates its size bound.");
        byte[] bytes = new byte[checked((int)info.Length)];
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.Length != info.Length) throw new IOException("A source artifact changed while reading.");
        return bytes;
    }

    private static async Task<Sha256Digest> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new(Convert.ToHexString(await SHA256.HashDataAsync(
            stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant());
    }

    private static RecoveryIssue ConvertIssue(ExecutionIssue issue, string scope) =>
        new($"RECOVERY.{issue.Code}", issue.Severity == ExecutionIssueSeverity.BlockingError
                ? RecoveryIssueSeverity.Blocking : RecoveryIssueSeverity.Information,
            scope, issue.Explanation,
            issue.RecordId is null ? null : new LogicalRecoveryRecordId($"record:{issue.RecordId.Value.Value}"),
            issue.RecordId,
            issue.Format);

    private static RecoverySourceInspection Failed(string bundle, RecoveryIssue issue) =>
        new(bundle, null, null, null, null, null, null, null, [], [issue]);

    private static RecoveryIssue Block(string code, string explanation) =>
        new(code, RecoveryIssueSeverity.Blocking, "Original recovery source", explanation);

    private static RecoveryIssue[] Ordered(IEnumerable<RecoveryIssue> values) =>
        values.OrderByDescending(value => value.Severity)
            .ThenBy(value => value.Code, StringComparer.Ordinal)
            .ThenBy(value => value.Subject, StringComparer.Ordinal).ToArray();

    private sealed record JournalLineDto(
        string Schema,
        int Sequence,
        string PreviousHash,
        string EntryHash,
        string ExecutionId,
        string PlanId,
        string PlanContentDigest,
        string LibraryUuid,
        string ApplicationVersion,
        ExecutionJournalEvent Event);

    private sealed record ToolIdentityDto(
        string CanonicalExecutablePath,
        string ProductVersion,
        string ExecutableSha256,
        string CapabilityProfile,
        string[] Capabilities);

    private sealed record ApplicationIdentityDto(string ApplicationVersion);

    private enum SourceOperationJournalStage
    {
        NotStarted,
        Started,
        CommandSucceeded,
        SemanticallyVerified,
        VerificationFailed,
        CommandFailed,
        Verified,
        SatisfiedNoOp,
    }
}
