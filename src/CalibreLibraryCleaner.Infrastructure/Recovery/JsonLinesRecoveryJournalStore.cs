using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Infrastructure.Execution;

namespace CalibreLibraryCleaner.Infrastructure.Recovery;

internal sealed class JsonLinesRecoveryJournalStore : IRecoveryJournalStore
{
    private const string Schema = "cleanup-recovery-journal/1.0";
    private static readonly HashSet<string> KnownEvents = new(StringComparer.Ordinal)
    {
        "JournalHeader",
        "RecoveryCreated",
        "CurrentStateBackupStarted",
        "CurrentStateBackupVerified",
        "MutationStarting",
        "OperationStarting",
        "CommandFinished",
        "RecordIdMapped",
        "RecordIdMappingFinalized",
        "OperationVerified",
        "OperationSatisfiedNoAction",
        "IntermediateVerificationStarted",
        "DestructiveRecoveryApproved",
        "DestructiveRecoveryDeclined",
        "DestructiveRecoveryStarting",
        "FinalVerificationStarted",
        "FinalVerificationPassed",
        "FinalVerificationFailed",
        "CancellationRequested",
        "RecoveryStopped",
        "TerminalSummary",
    };
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new RecoveryRecordIdMappingConverter(),
            new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false),
        },
    };

    public async Task<IRecoveryJournalSession> CreateAsync(
        RecoveryJournalCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Directory.Exists(request.BundleIdentity)
            || !ExecutionPathGuard.TryRejectReparsePoints(request.BundleIdentity, true, out _))
            throw new IOException("The recovery journal workspace is unsafe.");
        string path = Path.Combine(request.BundleIdentity, "recovery.journal.jsonl");
        Session session = new(new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough),
            path, request);
        try
        {
            await session.AppendAsync(new("JournalHeader", RecoveryExecutionState.Created,
                request.CreatedAtUtc, "Versioned recovery journal created.",
                CanonicalRootIdentityDigest:
                    request.Plan.Definition.InputIdentity.CanonicalRootIdentityDigest.Value,
                SourceJournalFileDigest:
                    request.Plan.Definition.InputIdentity.SourceJournalFileDigest.Value,
                OriginalManifestFileDigest:
                    request.Plan.Definition.InputIdentity.OriginalManifestFileDigest.Value),
                cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<RecoveryJournalReconciliationResult> ReconcileAsync(
        string canonicalDestinationIdentity,
        string libraryUuid,
        CancellationToken cancellationToken)
    {
        List<RecoveryIssue> issues = [];
        bool manual = false;
        if (!Directory.Exists(canonicalDestinationIdentity)) return new(false, []);
        foreach (string directory in Directory.EnumerateDirectories(
                     canonicalDestinationIdentity, "recovery-*", SearchOption.TopDirectoryOnly)
                 .Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(directory, "recovery.journal.jsonl");
            if (!File.Exists(path)) continue;
            Reconciled value = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            if (value.LibraryUuid is not null
                && !string.Equals(value.LibraryUuid, libraryUuid, StringComparison.Ordinal)) continue;
            bool summaryMatches = value.HasTerminalSummary
                && await SummaryMatchesAsync(directory, value, cancellationToken)
                    .ConfigureAwait(false);
            bool terminalWithoutConcern = value.Valid && summaryMatches
                && (value.LastState == RecoveryExecutionState.Recovered
                    || !value.MutationStarted
                    && value.LastState is RecoveryExecutionState.CancelledBeforeMutation
                        or RecoveryExecutionState.RecoveryFailed
                        or RecoveryExecutionState.Revoked
                        or RecoveryExecutionState.Stale);
            if (!terminalWithoutConcern)
            {
                manual = true;
                issues.Add(Block(value.Valid
                        ? "RECOVERY.UNRESOLVED_JOURNAL" : "RECOVERY.CORRUPT_JOURNAL",
                    "Recovery journal",
                    value.Valid
                        ? "A previous recovery has no safely resolved terminal state."
                        : "A recovery journal is corrupt or truncated."));
            }
        }
        return new(manual, issues);
    }

    internal static async Task<bool> VerifyRecoveredAsync(
        string journalPath,
        RecoveryResolutionEntry expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        try
        {
            string path = Path.GetFullPath(journalPath);
            string directory = Path.GetDirectoryName(path)
                ?? throw new IOException("The recovery journal has no directory.");
            Reconciled journal = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            return journal.Valid && journal.HasTerminalSummary
                && journal.LastState == RecoveryExecutionState.Recovered
                && await SummaryMatchesAsync(directory, journal, cancellationToken)
                    .ConfigureAwait(false)
                && journal.RecoveryExecutionId == expected.RecoveryExecutionId.ToString()
                && journal.RecoveryPlanId == expected.RecoveryPlanId.ToString()
                && journal.SourceExecutionId == expected.SourceExecutionId.ToString()
                && journal.LibraryUuid == expected.LibraryUuid
                && journal.FinalEntryHash == expected.RecoveryJournalFinalHash
                && journal.CurrentStateManifestFileDigest
                    == expected.CurrentStateManifestFileDigest
                && journal.CurrentStateManifestInternalDigest
                    == expected.CurrentStateManifestInternalDigest
                && journal.CanonicalRootIdentityDigest
                    == expected.CanonicalRootIdentityDigest
                && journal.SourceJournalFileDigest
                    == expected.SourceJournalFileDigest
                && journal.OriginalManifestFileDigest
                    == expected.OriginalManifestFileDigest;
        }
        catch (Exception exception) when (exception is IOException
                   or UnauthorizedAccessException or JsonException
                   or ArgumentException or InvalidOperationException
                   or NotSupportedException)
        {
            return false;
        }
    }

    internal static async Task<bool> VerifyTerminalAsync(
        RecoveryHistoryEntry expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        try
        {
            string path = Path.GetFullPath(expected.JournalIdentity);
            string directory = Path.GetDirectoryName(path)
                ?? throw new IOException("The recovery journal has no directory.");
            Reconciled journal = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
            bool safelyTerminal = expected.State == RecoveryExecutionState.Recovered
                || !expected.MutationStarted
                && expected.State is RecoveryExecutionState.CancelledBeforeMutation
                    or RecoveryExecutionState.RecoveryFailed
                    or RecoveryExecutionState.Revoked
                    or RecoveryExecutionState.Stale;
            return safelyTerminal && journal.Valid && journal.HasTerminalSummary
                && journal.LastState == expected.State
                && await SummaryMatchesAsync(directory, journal, cancellationToken)
                    .ConfigureAwait(false)
                && journal.RecoveryExecutionId == expected.RecoveryExecutionId.ToString()
                && journal.RecoveryPlanId == expected.RecoveryPlanId.ToString()
                && journal.RecoveryPlanContentDigest
                    == expected.RecoveryPlanContentDigest.Value
                && journal.SourceExecutionId == expected.SourceExecutionId.ToString()
                && journal.LibraryUuid == expected.LibraryUuid
                && journal.MutationStarted == expected.MutationStarted
                && journal.DestructiveRecoveryStarted
                    == expected.DestructiveRecoveryStarted
                && journal.CurrentStateManifestInternalDigest
                    == expected.CurrentStateManifestDigest
                && journal.CurrentStateManifestFileDigest
                    == expected.CurrentStateManifestFileDigest
                && journal.CanonicalRootIdentityDigest
                    == expected.CanonicalRootIdentityDigest
                && journal.SourceJournalFileDigest
                    == expected.SourceJournalFileDigest
                && journal.OriginalManifestFileDigest
                    == expected.OriginalManifestFileDigest;
        }
        catch (Exception exception) when (exception is IOException
                   or UnauthorizedAccessException or JsonException
                   or ArgumentException or InvalidOperationException
                   or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<Reconciled> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string previous = new('0', 64);
        RecoveryExecutionState? lastState = null;
        string? libraryUuid = null;
        string? recoveryExecutionId = null;
        string? recoveryPlanId = null;
        string? recoveryPlanDigest = null;
        string? sourceExecutionId = null;
        bool mutation = false;
        bool mutationBoundary = false;
        bool backupVerified = false;
        bool destructiveApproved = false;
        bool destructiveStarted = false;
        bool intermediateStarted = false;
        bool finalStarted = false;
        bool finalPassed = false;
        bool commandFailed = false;
        bool terminal = false;
        string? finalEntryHash = null;
        string? terminalFailureCode = null;
        string? currentManifestFileDigest = null;
        string? currentManifestInternalDigest = null;
        string? canonicalRootIdentityDigest = null;
        string? sourceJournalFileDigest = null;
        string? originalManifestFileDigest = null;
        DateTimeOffset? terminalOccurredAtUtc = null;
        RecoveryPlan? plan = null;
        Dictionary<RecoveryOperationId, JournalOperationStage>? operations = null;
        RecoveryOperationId? activeOperation = null;
        List<RecoveryRecordIdMapping> mappings = [];
        HashSet<LogicalRecoveryRecordId> finalizedMappings = [];
        try
        {
            if (!ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
                return Invalid();
            string directory = Path.GetDirectoryName(path)
                ?? throw new IOException("The recovery journal has no directory.");
            string planPath = Path.Combine(directory, "approved.recovery-plan.json");
            if (File.Exists(planPath))
            {
                if (!ExecutionPathGuard.TryRejectReparsePoints(planPath, true, out _))
                    return Invalid();
                FileInfo planInfo = new(planPath);
                if (planInfo.Length is <= 0 or > 64L * 1024 * 1024)
                    return Invalid();
                byte[] planBytes = new byte[checked((int)planInfo.Length)];
                await using FileStream planStream = new(planPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await planStream.ReadExactlyAsync(planBytes, cancellationToken).ConfigureAwait(false);
                plan = RecoveryPlanArtifactCodec.Deserialize(planBytes);
                if (RecoveryPlanContentDigestPolicy.Compute(plan.Definition) != plan.ContentDigest)
                    return Invalid();
                operations = plan.Definition.OperationGraph.Operations.ToDictionary(
                    operation => operation.Id, _ => JournalOperationStage.Planned);
            }

            using StreamReader reader = new(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan), Encoding.UTF8, false);
            int sequence = 1;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } text)
            {
                if (text.Length is <= 0 or > 1_000_000 || sequence > 100_000)
                    return Invalid();
                JournalLine? line = JsonSerializer.Deserialize<JournalLine>(text, Options);
                if (line is null) return Invalid();
                if (line.Schema != Schema) return Invalid();
                if (line.Sequence != sequence) return Invalid();
                if (line.PreviousHash != previous) return Invalid();
                if (HashSerializedLine(text, line.EntryHash) != line.EntryHash)
                    return Invalid();
                if (!KnownEvents.Contains(line.Event.Kind)) return Invalid();
                if (terminal) return Invalid();
                if (lastState is not null && line.Event.State != lastState
                    && !RecoveryExecution.IsLegal(lastState.Value, line.Event.State))
                    return Invalid();
                libraryUuid ??= line.LibraryUuid;
                recoveryExecutionId ??= line.RecoveryExecutionId;
                recoveryPlanId ??= line.RecoveryPlanId;
                recoveryPlanDigest ??= line.RecoveryPlanContentDigest;
                sourceExecutionId ??= line.SourceExecutionId;
                if (libraryUuid != line.LibraryUuid
                    || recoveryExecutionId != line.RecoveryExecutionId
                    || recoveryPlanId != line.RecoveryPlanId
                    || recoveryPlanDigest != line.RecoveryPlanContentDigest
                    || sourceExecutionId != line.SourceExecutionId)
                    return Invalid();
                if (sequence == 1 && line.Event.Kind != "JournalHeader"
                    || sequence > 1 && line.Event.Kind == "JournalHeader"
                    || line.Event.MutationStarted && !mutationBoundary
                       && line.Event.Kind != "MutationStarting"
                    || line.Event.DestructiveRecoveryStarted && !destructiveStarted
                       && line.Event.Kind != "DestructiveRecoveryStarting")
                    return Invalid();
                if (plan is not null
                    && (line.RecoveryPlanId != plan.Id.ToString()
                        || line.RecoveryPlanContentDigest != plan.ContentDigest.Value
                        || line.SourceExecutionId
                        != plan.Definition.InputIdentity.SourceExecutionId.ToString()
                        || line.LibraryUuid
                        != plan.Definition.InputIdentity.CurrentLibraryUuid))
                    return Invalid();

                RecoveryJournalEvent journalEvent = line.Event;
                switch (journalEvent.Kind)
                {
                    case "JournalHeader":
                        canonicalRootIdentityDigest =
                            journalEvent.CanonicalRootIdentityDigest;
                        sourceJournalFileDigest =
                            journalEvent.SourceJournalFileDigest;
                        originalManifestFileDigest =
                            journalEvent.OriginalManifestFileDigest;
                        if (canonicalRootIdentityDigest is null)
                            return Invalid();
                        if (sourceJournalFileDigest is null)
                            return Invalid();
                        if (originalManifestFileDigest is null)
                            return Invalid();
                        if (plan is not null && canonicalRootIdentityDigest
                            != plan.Definition.InputIdentity
                                .CanonicalRootIdentityDigest.Value)
                            return Invalid();
                        if (plan is not null && sourceJournalFileDigest
                            != plan.Definition.InputIdentity
                                .SourceJournalFileDigest.Value)
                            return Invalid();
                        if (plan is not null && originalManifestFileDigest
                            != plan.Definition.InputIdentity
                                .OriginalManifestFileDigest.Value)
                            return Invalid();
                        break;
                    case "CurrentStateBackupVerified":
                        if (backupVerified || mutationBoundary
                            || journalEvent.CurrentStateManifestFileDigest is null
                            || journalEvent.CurrentStateManifestInternalDigest is null)
                            return Invalid();
                        backupVerified = true;
                        currentManifestFileDigest =
                            journalEvent.CurrentStateManifestFileDigest;
                        currentManifestInternalDigest =
                            journalEvent.CurrentStateManifestInternalDigest;
                        break;
                    case "MutationStarting":
                        if (!backupVerified || mutationBoundary)
                            return Invalid();
                        mutationBoundary = true;
                        mutation = true;
                        break;
                    case "OperationSatisfiedNoAction":
                        if (!TryGetOperation(journalEvent.OperationId,
                                RecoveryOperationPhase.NonMutating,
                                JournalOperationStage.Planned,
                                out RecoveryOperation? satisfied)
                            || !DependenciesSatisfied(satisfied!))
                            return Invalid();
                        operations![satisfied!.Id] = JournalOperationStage.Satisfied;
                        break;
                    case "OperationStarting":
                        if (!mutationBoundary || commandFailed || activeOperation is not null
                            || !TryGetOperation(journalEvent.OperationId, null,
                                JournalOperationStage.Planned,
                                out RecoveryOperation? starting)
                            || starting!.Phase == RecoveryOperationPhase.NonMutating
                            || !DependenciesSatisfied(starting)
                            || starting.Phase == RecoveryOperationPhase.Destructive
                               && !destructiveStarted)
                            return Invalid();
                        activeOperation = starting.Id;
                        operations![starting.Id] = JournalOperationStage.Started;
                        break;
                    case "CommandFinished":
                        if (journalEvent.OperationId is null
                            || activeOperation != journalEvent.OperationId
                            || operations is null
                            || operations[journalEvent.OperationId]
                            != JournalOperationStage.Started)
                            return Invalid();
                        if (journalEvent.ExitCode == 0
                            && journalEvent.FailureCode is null)
                            operations[journalEvent.OperationId] =
                                JournalOperationStage.CommandSucceeded;
                        else
                        {
                            operations[journalEvent.OperationId] =
                                JournalOperationStage.CommandFailed;
                            commandFailed = true;
                            activeOperation = null;
                        }
                        break;
                    case "RecordIdMapped":
                        if (journalEvent.OperationId is null
                            || journalEvent.RecordIdMapping is null
                            || activeOperation != journalEvent.OperationId
                            || operations is null
                            || operations[journalEvent.OperationId]
                            != JournalOperationStage.CommandSucceeded
                            || plan?.Definition.OperationGraph.Operations.Single(
                                    value => value.Id == journalEvent.OperationId).Kind
                                != RecoveryOperationKind.CreateRecoveredRecord
                            || !InitialMappingMatchesPlan(
                                plan.Definition.OperationGraph.Operations.Single(
                                    value => value.Id == journalEvent.OperationId),
                                journalEvent.RecordIdMapping)
                            || mappings.Any(value =>
                                value.LogicalRecordId
                                    == journalEvent.RecordIdMapping.LogicalRecordId
                                || value.RecoveredRecordId
                                    == journalEvent.RecordIdMapping.RecoveredRecordId))
                            return Invalid();
                        mappings.Add(journalEvent.RecordIdMapping);
                        break;
                    case "OperationVerified":
                        if (journalEvent.OperationId is null
                            || activeOperation != journalEvent.OperationId
                            || operations is null
                            || operations[journalEvent.OperationId]
                            != JournalOperationStage.CommandSucceeded
                            || plan!.Definition.OperationGraph.Operations.Single(
                                    value => value.Id == journalEvent.OperationId).Kind
                                == RecoveryOperationKind.CreateRecoveredRecord
                               && mappings.All(value =>
                                   value.LogicalRecordId
                                   != plan.Definition.OperationGraph.Operations.Single(
                                       operation =>
                                           operation.Id
                                           == journalEvent.OperationId).LogicalRecordId))
                            return Invalid();
                        operations[journalEvent.OperationId] =
                            JournalOperationStage.Verified;
                        activeOperation = null;
                        break;
                    case "IntermediateVerificationStarted":
                        if (activeOperation is not null || intermediateStarted
                            || operations is null
                            || plan!.Definition.OperationGraph.ConstructiveOperations.Any(
                                operation => operations[operation.Id]
                                    != JournalOperationStage.Verified))
                            return Invalid();
                        intermediateStarted = true;
                        break;
                    case "DestructiveRecoveryApproved":
                        if (!backupVerified || !intermediateStarted
                            || destructiveApproved
                            || journalEvent.DestructiveOperationDigest is null
                            || plan is null
                            || journalEvent.DestructiveOperationDigest
                                != RecoveryOperationGraph.ComputeCanonicalDigest(
                                    plan.Definition.OperationGraph
                                        .DestructiveOperations).Value)
                            return Invalid();
                        destructiveApproved = true;
                        break;
                    case "DestructiveRecoveryDeclined":
                        if (!intermediateStarted || destructiveStarted)
                            return Invalid();
                        break;
                    case "DestructiveRecoveryStarting":
                        if (!destructiveApproved || !intermediateStarted
                            || !mutationBoundary || destructiveStarted)
                            return Invalid();
                        destructiveStarted = true;
                        break;
                    case "FinalVerificationStarted":
                        if (activeOperation is not null || finalStarted
                            || operations is null
                            || operations.Values.Any(value =>
                                value is not (JournalOperationStage.Verified
                                    or JournalOperationStage.Satisfied)))
                            return Invalid();
                        finalStarted = true;
                        break;
                    case "FinalVerificationPassed":
                        if (!finalStarted || finalPassed
                            || journalEvent.State != RecoveryExecutionState.Recovered)
                            return Invalid();
                        finalPassed = true;
                        break;
                    case "FinalVerificationFailed":
                        if (!finalStarted || finalPassed
                            || journalEvent.State == RecoveryExecutionState.Recovered)
                            return Invalid();
                        break;
                    case "RecoveryStopped":
                        if (journalEvent.OperationId is null)
                        {
                            if (activeOperation is not null) return Invalid();
                            break;
                        }
                        if (operations is null
                            || !operations.TryGetValue(
                                journalEvent.OperationId,
                                out JournalOperationStage stoppedStage)
                            || stoppedStage is not (
                                JournalOperationStage.Started
                                or JournalOperationStage.CommandSucceeded
                                or JournalOperationStage.CommandFailed)
                            || stoppedStage != JournalOperationStage.CommandFailed
                               && activeOperation != journalEvent.OperationId
                            || stoppedStage == JournalOperationStage.CommandFailed
                               && activeOperation is not null)
                            return Invalid();
                        operations[journalEvent.OperationId] =
                            JournalOperationStage.Failed;
                        activeOperation = null;
                        break;
                    case "RecordIdMappingFinalized":
                        if (!finalPassed
                            || journalEvent.State
                            != RecoveryExecutionState.Recovered
                            || journalEvent.OperationId is not null
                            || journalEvent.RecordIdMapping is null
                            || plan is null
                            || !FinalizedMappingMatchesPlan(
                                journalEvent.RecordIdMapping)
                            || finalizedMappings.Contains(
                                journalEvent.RecordIdMapping.LogicalRecordId))
                            return Invalid();
                        finalizedMappings.Add(
                            journalEvent.RecordIdMapping.LogicalRecordId);
                        break;
                    case "TerminalSummary":
                        if (activeOperation is not null
                            || journalEvent.State == RecoveryExecutionState.Recovered
                               && (!finalPassed
                                   || finalizedMappings.Count != mappings.Count))
                            return Invalid();
                        terminal = true;
                        finalEntryHash = line.EntryHash;
                        terminalFailureCode = journalEvent.FailureCode;
                        terminalOccurredAtUtc = journalEvent.OccurredAtUtc;
                        break;
                }

                mutation |= journalEvent.MutationStarted;
                if (mutation && !backupVerified) return Invalid();
                lastState = line.Event.State;
                previous = line.EntryHash;
                sequence++;
            }
            if (backupVerified
                && !await VerifyCurrentStateBackupAsync(
                    directory, recoveryExecutionId, recoveryPlanId,
                    recoveryPlanDigest, libraryUuid,
                    currentManifestFileDigest, currentManifestInternalDigest,
                    cancellationToken).ConfigureAwait(false))
                return Invalid();
            return new(lastState is not null, libraryUuid, recoveryExecutionId,
                recoveryPlanId, recoveryPlanDigest, sourceExecutionId,
                mutation, destructiveStarted, terminal, lastState, finalEntryHash,
                terminalFailureCode, currentManifestFileDigest,
                currentManifestInternalDigest, canonicalRootIdentityDigest,
                sourceJournalFileDigest, originalManifestFileDigest,
                terminalOccurredAtUtc,
                mappings.Any(value => value.NumericIdChanged),
                plan?.Definition.ExpectedFinalState.PreservedContent.Count > 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                   or JsonException or ArgumentException or InvalidOperationException
                   or NotSupportedException)
        {
            return Invalid();
        }

        Reconciled Invalid() => new(false, libraryUuid, recoveryExecutionId,
            recoveryPlanId, recoveryPlanDigest, sourceExecutionId,
            mutation, destructiveStarted, terminal, lastState, finalEntryHash,
            terminalFailureCode, currentManifestFileDigest,
            currentManifestInternalDigest, canonicalRootIdentityDigest,
            sourceJournalFileDigest, originalManifestFileDigest,
            terminalOccurredAtUtc, false, false);

        bool TryGetOperation(
            RecoveryOperationId? operationId,
            RecoveryOperationPhase? expectedPhase,
            JournalOperationStage expectedStage,
            out RecoveryOperation? operation)
        {
            operation = null;
            if (operationId is null || operations is null || plan is null
                || !operations.TryGetValue(operationId, out JournalOperationStage stage)
                || stage != expectedStage)
                return false;
            operation = plan.Definition.OperationGraph.Operations.Single(
                value => value.Id == operationId);
            return expectedPhase is null || operation.Phase == expectedPhase;
        }

        bool DependenciesSatisfied(RecoveryOperation operation) =>
            operations is not null
            && operation.DependencyIds.Concat(operation.PreservationDependencyIds)
                .All(dependency => operations.TryGetValue(
                        dependency, out JournalOperationStage stage)
                    && stage is JournalOperationStage.Verified
                        or JournalOperationStage.Satisfied);

        bool InitialMappingMatchesPlan(
            RecoveryOperation operation,
            RecoveryRecordIdMapping mapping)
        {
            if (plan is null
                || mapping.LogicalRecordId != operation.LogicalRecordId
                || mapping.OriginalRecordId != operation.OriginalRecordId
                || mapping.RestoredFormats.Count != 0)
                return false;
            CalibreBookId[] cleanupTargets = plan.Definition.Reconciliation
                .SourceOperations
                .Where(value =>
                    value.SourceRecordId == operation.OriginalRecordId)
                .Select(value => value.TargetRecordId)
                .Distinct()
                .ToArray();
            CalibreBookId? expectedCleanupTarget = cleanupTargets.Length switch
            {
                0 => operation.CurrentRecordId,
                1 => cleanupTargets[0],
                _ => null,
            };
            return cleanupTargets.Length <= 1
                && mapping.CleanupTargetRecordId == expectedCleanupTarget;
        }

        bool FinalizedMappingMatchesPlan(RecoveryRecordIdMapping finalized)
        {
            RecoveryRecordIdMapping? initial = mappings.SingleOrDefault(value =>
                value.LogicalRecordId == finalized.LogicalRecordId);
            ExpectedRecoveredRecordState? expected =
                plan?.Definition.ExpectedFinalState.Records.SingleOrDefault(
                    value =>
                        value.LogicalRecordId == finalized.LogicalRecordId);
            if (initial is null || expected is null
                || finalized.OriginalRecordId != initial.OriginalRecordId
                || finalized.CleanupTargetRecordId
                != initial.CleanupTargetRecordId
                || finalized.RecoveredRecordId != initial.RecoveredRecordId
                || finalized.MappedAtUtc != initial.MappedAtUtc)
                return false;
            string[] formats = expected.OriginalState.Formats
                .Select(value => value.Format)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] identifiers = expected.OriginalState.Identifiers
                .Select(value => $"{value.Type}:{value.Value}")
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            return finalized.RestoredFormats.SequenceEqual(
                    formats, StringComparer.Ordinal)
                && finalized.Identifiers.SequenceEqual(
                    identifiers, StringComparer.Ordinal);
        }

    }

    private static async Task<bool> VerifyCurrentStateBackupAsync(
        string directory,
        string? recoveryExecutionId,
        string? recoveryPlanId,
        string? recoveryPlanContentDigest,
        string? libraryUuid,
        string? expectedFileDigest,
        string? expectedInternalDigest,
        CancellationToken cancellationToken)
    {
        try
        {
            string path = Path.Combine(
                directory, "current-state-backup-manifest.json");
            if (!File.Exists(path)
                || !ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
                return false;
            FileInfo info = new(path);
            if (info.Length is <= 0 or > 64L * 1024 * 1024)
                return false;
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] bytes = new byte[checked((int)info.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            string fileDigest = Convert.ToHexString(
                SHA256.HashData(bytes)).ToLowerInvariant();
            CurrentStateManifestDto? manifest =
                JsonSerializer.Deserialize<CurrentStateManifestDto>(bytes, Options);
            if (manifest is null
                || manifest.SchemaVersion
                    != "cleanup-recovery-current-state-backup/1.0"
                || manifest.RecoveryExecutionId != recoveryExecutionId
                || manifest.RecoveryPlanId != recoveryPlanId
                || manifest.RecoveryPlanContentDigest
                    != recoveryPlanContentDigest
                || manifest.LibraryUuid != libraryUuid
                || fileDigest != expectedFileDigest
                || manifest.ManifestInternalDigest != expectedInternalDigest
                || ComputeCurrentStateManifestDigest(manifest)
                    != expectedInternalDigest
                || manifest.Entries.Select(value => value.RelativePath)
                    .Distinct(StringComparer.Ordinal).Count()
                    != manifest.Entries.Length
                || !manifest.Entries.Select(value => value.RelativePath)
                    .SequenceEqual(manifest.Entries.Select(value => value.RelativePath)
                        .Order(StringComparer.Ordinal), StringComparer.Ordinal))
                return false;
            foreach (CurrentStateManifestEntryDto entry in manifest.Entries)
            {
                if (entry.SizeInBytes < 0 || !IsSafeRelative(entry.RelativePath))
                    return false;
                string artifactPath = Path.GetFullPath(Path.Combine(directory,
                    entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!ExecutionPathGuard.IsContained(directory, artifactPath)
                    || !File.Exists(artifactPath)
                    || !ExecutionPathGuard.TryRejectReparsePoints(
                        artifactPath, true, out _))
                    return false;
                FileInfo artifact = new(artifactPath);
                if (artifact.Length != entry.SizeInBytes)
                    return false;
                await using FileStream artifactStream = new(artifactPath,
                    FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                string artifactDigest = Convert.ToHexString(
                    await SHA256.HashDataAsync(
                        artifactStream, cancellationToken).ConfigureAwait(false))
                    .ToLowerInvariant();
                if (artifactDigest != entry.Sha256) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException
                   or UnauthorizedAccessException or JsonException
                   or ArgumentException or InvalidOperationException
                   or NotSupportedException or CryptographicException
                   or OverflowException)
        {
            return false;
        }
    }

    private static string ComputeCurrentStateManifestDigest(
        CurrentStateManifestDto manifest)
    {
        StringBuilder value = new();
        Add(value, manifest.SchemaVersion);
        Add(value, manifest.RecoveryExecutionId);
        Add(value, manifest.RecoveryPlanId);
        Add(value, manifest.RecoveryPlanContentDigest);
        Add(value, manifest.LibraryUuid);
        Add(value, manifest.SourceStateFingerprint);
        foreach (CurrentStateManifestEntryDto entry in manifest.Entries)
        {
            Add(value, entry.RelativePath);
            Add(value, entry.Kind.ToString());
            Add(value, entry.SizeInBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            Add(value, entry.Sha256);
            Add(value, entry.LogicalRecordId ?? string.Empty);
            Add(value, entry.CurrentRecordId?.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
                ?? string.Empty);
            Add(value, entry.Format ?? string.Empty);
            Add(value, entry.PreservationRole ?? string.Empty);
        }
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(value.ToString()))).ToLowerInvariant();
    }

    private static bool IsSafeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)
            || Path.IsPathRooted(relative)) return false;
        string[] segments = relative.Replace('\\', '/').Split('/');
        return segments.All(value => value.Length > 0
            && value is not "." and not "..");
    }

    private static void Add(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value).Append(';');

    private static async Task<bool> SummaryMatchesAsync(
        string directory,
        Reconciled journal,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, "recovery-summary.json");
        try
        {
            if (!File.Exists(path)
                || !ExecutionPathGuard.TryRejectReparsePoints(path, true, out _))
                return false;
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > 4L * 1024 * 1024) return false;
            RecoveryHistoryEntry? summary = await JsonSerializer.DeserializeAsync<
                RecoveryHistoryEntry>(stream, Options, cancellationToken).ConfigureAwait(false);
            return summary is not null
                && summary.RecoveryExecutionId.ToString() == journal.RecoveryExecutionId
                && summary.RecoveryPlanId.ToString() == journal.RecoveryPlanId
                && summary.RecoveryPlanContentDigest.Value == journal.RecoveryPlanContentDigest
                && summary.SourceExecutionId.ToString() == journal.SourceExecutionId
                && summary.LibraryUuid == journal.LibraryUuid
                && summary.State == journal.LastState
                && summary.MutationStarted == journal.MutationStarted
                && summary.DestructiveRecoveryStarted == journal.DestructiveRecoveryStarted
                && summary.BundleIdentity == directory
                && summary.JournalIdentity == Path.Combine(
                    directory, "recovery.journal.jsonl")
                && summary.CurrentStateManifestDigest
                    == journal.CurrentStateManifestInternalDigest
                && summary.CurrentStateManifestFileDigest
                    == journal.CurrentStateManifestFileDigest
                && summary.CanonicalRootIdentityDigest
                    == journal.CanonicalRootIdentityDigest
                && summary.SourceJournalFileDigest
                    == journal.SourceJournalFileDigest
                && summary.OriginalManifestFileDigest
                    == journal.OriginalManifestFileDigest
                && summary.FinishedAtUtc == journal.TerminalOccurredAtUtc
                && summary.FailureClassification.ToString()
                    == (journal.TerminalFailureCode ?? RecoveryFailureClassification.None.ToString())
                && summary.ChangedRecordIds == journal.ChangedRecordIds
                && summary.PreservedUnexpectedContent
                    == journal.PreservedUnexpectedContent;
        }
        catch (Exception exception) when (exception is IOException
                   or UnauthorizedAccessException or JsonException
                   or ArgumentException)
        {
            return false;
        }
    }

    private sealed class Session(
        FileStream stream,
        string path,
        RecoveryJournalCreateRequest request) : IRecoveryJournalSession
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private FileStream? _stream = stream;
        private int _sequence;
        private string _previousHash = new('0', 64);

        public string JournalIdentity { get; } = path;
        public string? FinalEntryHash { get; private set; }
        public bool IsAvailable => _stream is not null;

        public async Task AppendAsync(
            RecoveryJournalEvent value,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(value);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                FileStream active = _stream
                    ?? throw new IOException("The recovery journal is unavailable.");
                JournalLine unsigned = new(Schema, ++_sequence, _previousHash, string.Empty,
                    request.RecoveryExecutionId.ToString(), request.Plan.Id.ToString(),
                    request.Plan.ContentDigest.Value,
                    request.Plan.Definition.InputIdentity.SourceExecutionId.ToString(),
                    request.Plan.Definition.InputIdentity.CurrentLibraryUuid,
                    request.ApplicationVersion, value);
                JournalLine complete = unsigned with { EntryHash = Hash(unsigned) };
                byte[] bytes = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(complete, Options) + "\n");
                await active.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                active.Flush(flushToDisk: true);
                _previousHash = complete.EntryHash;
                if (value.Kind == "TerminalSummary") FinalEntryHash = complete.EntryHash;
            }
            finally { _gate.Release(); }
        }

        public async Task CompleteAsync(
            RecoveryHistoryEntry value,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(value);
            string summaryPath = Path.Combine(request.BundleIdentity, "recovery-summary.json");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
            await using FileStream output = new(summaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            await AppendAsync(new("TerminalSummary", value.State, value.FinishedAtUtc,
                "Terminal recovery state persisted.",
                FailureCode: value.FailureClassification.ToString(),
                MutationStarted: value.MutationStarted,
                DestructiveRecoveryStarted: value.DestructiveRecoveryStarted),
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                FileStream? active = Interlocked.Exchange(ref _stream, null);
                if (active is not null)
                {
                    active.Flush(flushToDisk: true);
                    await active.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }
    }

    private static string Hash(JournalLine line) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            line with { EntryHash = string.Empty }, Options))).ToLowerInvariant();

    private static string HashSerializedLine(string serialized, string entryHash)
    {
        string marker = $"\"entryHash\":\"{entryHash}\"";
        int index = serialized.IndexOf(marker, StringComparison.Ordinal);
        if (entryHash.Length != 64 || index < 0
            || serialized.IndexOf(marker, index + marker.Length,
                StringComparison.Ordinal) >= 0)
            return string.Empty;
        string unsigned = string.Concat(serialized.AsSpan(0, index),
            "\"entryHash\":\"\"",
            serialized.AsSpan(index + marker.Length));
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(unsigned))).ToLowerInvariant();
    }

    private static RecoveryIssue Block(string code, string subject, string explanation) =>
        new(code, RecoveryIssueSeverity.Blocking, subject, explanation);

    private sealed record JournalLine(
        string Schema,
        int Sequence,
        string PreviousHash,
        string EntryHash,
        string RecoveryExecutionId,
        string RecoveryPlanId,
        string RecoveryPlanContentDigest,
        string SourceExecutionId,
        string LibraryUuid,
        string ApplicationVersion,
        RecoveryJournalEvent Event);

    private sealed record CurrentStateManifestDto(
        string SchemaVersion,
        string RecoveryExecutionId,
        string RecoveryPlanId,
        string RecoveryPlanContentDigest,
        string LibraryUuid,
        string SourceStateFingerprint,
        DateTimeOffset CreatedAtUtc,
        string ManifestInternalDigest,
        CurrentStateManifestEntryDto[] Entries);

    private sealed record CurrentStateManifestEntryDto(
        string RelativePath,
        RecoverySourceArtifactKind Kind,
        long SizeInBytes,
        string Sha256,
        string? LogicalRecordId,
        long? CurrentRecordId,
        string? Format,
        string? PreservationRole);

    private sealed class RecoveryRecordIdMappingConverter
        : JsonConverter<RecoveryRecordIdMapping>
    {
        public override RecoveryRecordIdMapping Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            RecoveryRecordIdMappingDto? value =
                JsonSerializer.Deserialize<RecoveryRecordIdMappingDto>(
                    ref reader, options);
            if (value is null
                || value.RestoredFormats is null
                || value.Identifiers is null
                || value.RestoredFormats.Length > 1_024
                || value.Identifiers.Length > 1_024
                || value.RestoredFormats.Any(item =>
                    string.IsNullOrWhiteSpace(item) || item.Length > 32)
                || value.Identifiers.Any(item =>
                    string.IsNullOrWhiteSpace(item) || item.Length > 4_096))
                throw new JsonException("The recovery record-ID mapping is invalid.");
            try
            {
                return new(new(value.LogicalRecordId),
                    new(value.OriginalRecordId),
                    value.CleanupTargetRecordId is { } cleanupTarget
                        ? new CalibreBookId(cleanupTarget)
                        : null,
                    new(value.RecoveredRecordId),
                    value.RestoredFormats,
                    value.Identifiers,
                    value.MappedAtUtc);
            }
            catch (Exception exception) when (exception is ArgumentException
                       or InvalidOperationException)
            {
                throw new JsonException(
                    "The recovery record-ID mapping is invalid.", exception);
            }
        }

        public override void Write(
            Utf8JsonWriter writer,
            RecoveryRecordIdMapping value,
            JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer,
                new RecoveryRecordIdMappingDto(
                    value.LogicalRecordId.Value,
                    value.OriginalRecordId.Value,
                    value.CleanupTargetRecordId?.Value,
                    value.RecoveredRecordId.Value,
                    value.RestoredFormats.ToArray(),
                    value.Identifiers.ToArray(),
                    value.MappedAtUtc),
                options);
    }

    private sealed record RecoveryRecordIdMappingDto(
        string LogicalRecordId,
        long OriginalRecordId,
        long? CleanupTargetRecordId,
        long RecoveredRecordId,
        string[] RestoredFormats,
        string[] Identifiers,
        DateTimeOffset MappedAtUtc);

    private sealed record Reconciled(
        bool Valid,
        string? LibraryUuid,
        string? RecoveryExecutionId,
        string? RecoveryPlanId,
        string? RecoveryPlanContentDigest,
        string? SourceExecutionId,
        bool MutationStarted,
        bool DestructiveRecoveryStarted,
        bool HasTerminalSummary,
        RecoveryExecutionState? LastState,
        string? FinalEntryHash,
        string? TerminalFailureCode,
        string? CurrentStateManifestFileDigest,
        string? CurrentStateManifestInternalDigest,
        string? CanonicalRootIdentityDigest,
        string? SourceJournalFileDigest,
        string? OriginalManifestFileDigest,
        DateTimeOffset? TerminalOccurredAtUtc,
        bool ChangedRecordIds,
        bool PreservedUnexpectedContent);

    private enum JournalOperationStage
    {
        Planned,
        Started,
        CommandSucceeded,
        CommandFailed,
        Failed,
        Verified,
        Satisfied,
    }
}
