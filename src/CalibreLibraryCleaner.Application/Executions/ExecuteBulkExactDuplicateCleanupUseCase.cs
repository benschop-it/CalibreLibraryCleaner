using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed partial class ExecuteBulkExactDuplicateCleanupUseCase(
    ILibraryStateSession libraryState,
    ICalibreToolDiscovery toolDiscovery,
    ICalibreMutationWorkerFactory workerFactory,
    ILibraryMutationLease mutationLease,
    ICleanupExecutionIdGenerator executionIds,
    IClock clock,
    ILogger<ExecuteBulkExactDuplicateCleanupUseCase>? logger = null)
{
    private readonly ILogger<ExecuteBulkExactDuplicateCleanupUseCase> _logger = logger
        ?? NullLogger<ExecuteBulkExactDuplicateCleanupUseCase>.Instance;

    public async Task<BulkExactDuplicateCleanupResult> ExecuteAsync(
        ExecuteBulkExactDuplicateCleanupRequest request,
        IProgress<BulkExactDuplicateCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CleanupExecutionId executionId = executionIds.Create();
        List<ExecutionIssue> issues = [];
        if (!request.ExternalBackupConfirmed)
        {
            issues.Add(Block("BULK_EXACT.BACKUP_NOT_CONFIRMED",
                "Confirm that a complete external library backup exists before removing duplicates."));
            LogPreflightFailure(_logger, executionId.ToString(), "BULK_EXACT.BACKUP_NOT_CONFIRMED");
            return Result(BulkExactDuplicateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }

        LibraryState? initial = libraryState.GetCurrent(request.LibraryRoot);
        if (initial is null || !initial.IsAuthoritative)
        {
            issues.Add(Block("BULK_EXACT.STATE_UNAVAILABLE",
                "Run an explicit scan before removing exact duplicates."));
            return Result(BulkExactDuplicateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }

        BulkPlan plan;
        try
        {
            plan = BuildPlan(initial.Snapshot, request.KeeperSelections, issues);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            issues.Add(Block("BULK_EXACT.PLAN_INVALID", exception.Message));
            return Result(BulkExactDuplicateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }
        if (plan.TotalOperations == 0)
            return Result(BulkExactDuplicateCleanupState.NothingToDo, 0, 0, 0, plan.SkippedRecordCount);

        LogCleanupStarted(_logger, executionId.ToString(), plan.TotalOperations);

        CalibreToolDiscoveryResult discovery = await toolDiscovery.DiscoverAndProbeAsync(
            request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        issues.AddRange(discovery.Issues);
        if (!discovery.IsSuccess)
            return Result(BulkExactDuplicateCleanupState.PreflightFailed, 0, 0, 0, plan.SkippedRecordCount);

        LibraryMutationLeaseAcquisition acquisition = await mutationLease.TryAcquireAsync(new(
            executionId.ToString(), LibraryMutationKind.Cleanup, request.LibraryRoot,
            initial.Snapshot.Identity.CalibreLibraryUuid, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        issues.AddRange(acquisition.Issues);
        if (!acquisition.IsAcquired)
            return Result(BulkExactDuplicateCleanupState.PreflightFailed, 0, 0, 0, plan.SkippedRecordCount);

        await using ILibraryMutationLeaseHandle lease = acquisition.Lease!;
        int completed = 0;
        int removedFormats = 0;
        int mergedRecords = 0;
        int removedRecords = 0;
        bool mutationStarted = false;
        try
        {
            CalibreMutationWorkerOpenResult worker = await workerFactory.TryOpenAsync(new(
                discovery.Tool!, request.LibraryRoot, initial.Snapshot.Identity.CalibreLibraryUuid),
                cancellationToken).ConfigureAwait(false);
            if (!worker.IsSuccess)
            {
                string failureCode = worker.FailureCode ?? "CALIBRE_WORKER_UNAVAILABLE";
                LogPreflightFailure(_logger, executionId.ToString(), failureCode);
                issues.Add(Block("BULK_EXACT.WORKER_UNAVAILABLE",
                    $"The persistent Calibre worker could not start ({failureCode})."));
                return Result(BulkExactDuplicateCleanupState.PreflightFailed, 0, 0, 0,
                    plan.SkippedRecordCount);
            }
            await using ICalibreMutationWorkerSession session = worker.Session!;
            return await ExecuteWithWorkerAsync(session).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(mutationStarted ? BulkExactDuplicateCleanupState.PartiallyCompleted
                : BulkExactDuplicateCleanupState.Cancelled, removedFormats, mergedRecords,
                removedRecords, plan.SkippedRecordCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidOperationException or ArgumentException)
        {
            if (mutationStarted)
                await MarkUncertainAsync("BULK_EXACT.UNEXPECTED_FAILURE", exception.Message, null).ConfigureAwait(false);
            LogMutationFailure(_logger, executionId.ToString(), "BULK_EXACT.UNEXPECTED_FAILURE", exception);
            issues.Add(Block("BULK_EXACT.EXECUTION_FAILED", exception.Message));
            return Result(mutationStarted ? BulkExactDuplicateCleanupState.PartiallyCompleted
                : BulkExactDuplicateCleanupState.PreflightFailed, removedFormats, mergedRecords,
                removedRecords, plan.SkippedRecordCount);
        }
        LibraryState Current() => libraryState.GetCurrent(request.LibraryRoot)
            ?? throw new InvalidOperationException("Authoritative projected state disappeared.");

        DateTimeOffset AppliedAt(LibraryState current)
        {
            DateTimeOffset now = clock.GetUtcNow().ToUniversalTime();
            return now < current.ProjectedAtUtc ? current.ProjectedAtUtc : now;
        }

        BulkExactDuplicateCleanupResult Result(
            BulkExactDuplicateCleanupState state,
            int formatCount,
            int mergeCount,
            int recordCount,
            int skippedCount) => new(executionId, state, formatCount, mergeCount,
            recordCount, skippedCount, issues.ToArray());

        BulkExactDuplicateCleanupResult FailProjection(string explanation)
        {
            LogMutationFailure(_logger, executionId.ToString(), "BULK_EXACT.DELTA_COMMIT_FAILED", null);
            issues.Add(Block("BULK_EXACT.DELTA_COMMIT_FAILED", explanation));
            return Result(BulkExactDuplicateCleanupState.PartiallyCompleted,
                removedFormats, mergedRecords, removedRecords, plan.SkippedRecordCount);
        }

        Task<LibraryStateSessionOutcome> MarkUncertainAsync(
            string code,
            string explanation,
            CalibreBookId? recordId) => libraryState.MarkUncertainAsync(request.LibraryRoot,
            new(code, explanation, clock.GetUtcNow(), recordId?.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)), CancellationToken.None);

        async Task<BulkExactDuplicateCleanupResult> ExecuteWithWorkerAsync(
            ICalibreMutationWorkerSession session)
        {
            using IDisposable statePublication = libraryState.DeferStateChanged(request.LibraryRoot);
            WorkerPlannedOperation[] operations = BuildWorkerOperations(plan);
            string mutationRunId = executionId.ToString();
            LibraryState markerState = Current();
            LibraryStateSessionOutcome marker = await libraryState.BeginMutationBatchAsync(
                request.LibraryRoot,
                new LibraryStateMutationIntent(
                    mutationRunId,
                    markerState.GenerationId,
                    markerState.Revision,
                    operations.Length,
                    clock.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            if (!marker.IsSuccess)
            {
                LogPreflightFailure(_logger, executionId.ToString(), "BULK_EXACT.MUTATION_MARKER_FAILED");
                issues.Add(Block("BULK_EXACT.MUTATION_MARKER_FAILED",
                    "A durable cleanup-run marker could not be written before Calibre mutation."));
                return Result(BulkExactDuplicateCleanupState.PreflightFailed,
                    removedFormats, mergedRecords, removedRecords, plan.SkippedRecordCount);
            }

            int chunkNumber = 0;
            foreach (WorkerPlannedOperation[] chunk in operations.Chunk(CalibreMutationChunkRequest.MaximumOperationCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lease.IsHeld) throw new InvalidOperationException("The cleanup lease was lost.");
                progress?.Report(new("Removing exact duplicates through the persistent Calibre worker.",
                    completed, plan.TotalOperations));
                string chunkId = $"{executionId}:{++chunkNumber}";
                CalibreMutationOperation[] chunkOperations = chunk.Select(value => value.Operation).ToArray();
                CalibreMutationChunkResult workerResult = await session.ExecuteChunkAsync(
                    new(chunkId, chunkOperations), CancellationToken.None).ConfigureAwait(false);
                mutationStarted |= workerResult.MutationStarted;
                if (!workerResult.IsSuccess)
                {
                    LogMutationFailure(_logger, executionId.ToString(),
                        workerResult.FailureCode ?? "BULK_EXACT.WORKER_CHUNK_FAILED", null);
                    await MarkUncertainAsync("BULK_EXACT.WORKER_CHUNK_FAILED",
                        "The persistent Calibre worker did not complete a mutation chunk unambiguously.",
                        null).ConfigureAwait(false);
                    issues.Add(Block("BULK_EXACT.WORKER_CHUNK_FAILED",
                        "The persistent Calibre worker could not complete duplicate cleanup."));
                    return Result(BulkExactDuplicateCleanupState.PartiallyCompleted,
                        removedFormats, mergedRecords, removedRecords, plan.SkippedRecordCount);
                }

                LibraryState current = Current();
                LibraryStateRevision revision = current.Revision;
                DateTimeOffset appliedAt = AppliedAt(current);
                List<LibraryStateDelta> deltas = new(chunk.Length);
                foreach (WorkerPlannedOperation planned in chunk)
                {
                    deltas.Add(CreateDelta(current.GenerationId, revision, appliedAt, planned));
                    revision = revision.Next();
                }
                LibraryStateSessionOutcome applied = await libraryState.ApplyMutationBatchAsync(
                    request.LibraryRoot, mutationRunId, deltas,
                    completeMutationIntent: completed + chunk.Length == operations.Length,
                    CancellationToken.None).ConfigureAwait(false);
                if (!applied.IsSuccess)
                {
                    return FailProjection("A worker mutation batch could not be committed to projected state.");
                }
                foreach (WorkerPlannedOperation planned in chunk)
                {
                    if (planned.Operation.Kind == CalibreMutationOperationKind.RemoveFormat) removedFormats++;
                    if (planned.Operation.Kind == CalibreMutationOperationKind.RemoveRecord)
                    {
                        if (plan.MergedRecordIds.Contains(planned.Operation.RecordId)) mergedRecords++;
                        removedRecords++;
                    }
                }
                completed += chunk.Length;
            }

            LibraryStateSessionOutcome checkpoint = await libraryState.CheckpointAsync(
                request.LibraryRoot, CancellationToken.None).ConfigureAwait(false);
            if (!checkpoint.IsSuccess)
                issues.Add(new("BULK_EXACT.CHECKPOINT_FAILED", ExecutionIssueSeverity.Warning,
                    "Duplicate cleanup completed, but projected state checkpoint compaction was deferred."));
            progress?.Report(new("Duplicate cleanup completed.", plan.TotalOperations, plan.TotalOperations));
            LogCleanupCompleted(_logger, executionId.ToString(), plan.TotalOperations);
            return Result(BulkExactDuplicateCleanupState.Completed, removedFormats,
                mergedRecords, removedRecords, plan.SkippedRecordCount);
        }
    }

    [LoggerMessage(1, LogLevel.Information,
        "Exact duplicate cleanup {ExecutionId} started with {OperationCount} operations.")]
    private static partial void LogCleanupStarted(ILogger logger, string executionId, int operationCount);

    [LoggerMessage(2, LogLevel.Warning,
        "Exact duplicate cleanup {ExecutionId} failed preflight with {FailureCode}.")]
    private static partial void LogPreflightFailure(ILogger logger, string executionId, string failureCode);

    [LoggerMessage(3, LogLevel.Error,
        "Exact duplicate cleanup {ExecutionId} mutation failed with {FailureCode}.")]
    private static partial void LogMutationFailure(
        ILogger logger,
        string executionId,
        string failureCode,
        Exception? exception);

    [LoggerMessage(4, LogLevel.Information,
        "Exact duplicate cleanup {ExecutionId} completed {OperationCount} operations.")]
    private static partial void LogCleanupCompleted(ILogger logger, string executionId, int operationCount);

    private static LibraryStateDelta CreateDelta(
        LibraryStateGenerationId generationId,
        LibraryStateRevision revision,
        DateTimeOffset appliedAt,
        WorkerPlannedOperation planned) => planned.Operation.Kind switch
        {
            CalibreMutationOperationKind.TransferFormat => new AddOrReplaceFormatLibraryStateDelta(
                generationId, revision, planned.Operation.OperationId, appliedAt,
                planned.Operation.TargetRecordId!.Value, planned.Operation.CanonicalFormat!,
                planned.Operation.ExpectedFingerprint!, null),
            CalibreMutationOperationKind.RemoveFormat => new RemoveFormatLibraryStateDelta(
                generationId, revision, planned.Operation.OperationId, appliedAt,
                planned.Operation.RecordId, planned.Operation.CanonicalFormat!,
                planned.Removal!.Format.Fingerprint!),
            CalibreMutationOperationKind.RemoveRecord => new RemoveRecordLibraryStateDelta(
                generationId, revision, planned.Operation.OperationId, appliedAt,
                planned.Operation.RecordId),
            _ => throw new InvalidOperationException("The worker returned an unsupported operation."),
        };

    private static WorkerPlannedOperation[] BuildWorkerOperations(BulkPlan plan) =>
    [
        .. plan.Transfers.Where(value => value.NeedsAdd).Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.TransferFormat(
                $"bulk-add:{value.TargetRecordId.Value}:{value.SourceFormat.Format}",
                value.SourceRecordId, value.TargetRecordId, value.SourceFormat.Format,
                value.SourceFormat.Fingerprint!), null)),
        .. plan.FormatRemovals.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.RemoveFormat(
                $"bulk-remove-format:{value.RecordId.Value}:{value.Format.Format}",
                value.RecordId, value.Format.Format), value)),
        .. plan.RecordsToRemove.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.RemoveRecord($"bulk-remove-record:{value.Value}", value), null)),
    ];

    internal static BulkPlan BuildPlan(
        LibrarySnapshot snapshot,
        IReadOnlyList<ExactDuplicateKeeperSelection> selections,
        List<ExecutionIssue> issues)
    {
        Dictionary<ExactBinaryDuplicateGroupId, ExactDuplicateKeeperSelection> selected = selections
            .GroupBy(value => value.GroupId)
            .ToDictionary(value => value.Key, value => value.Single());
        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books.ToDictionary(value => value.Id);
        Dictionary<FormatKey, FormatRemovalOperation> exactRemovals = [];
        Dictionary<CalibreBookId, HashSet<CalibreBookId>> sourceTargets = [];
        HashSet<CalibreBookId> retainedRecords = [];

        foreach (ExactBinaryDuplicateGroup group in snapshot.ExactBinaryDuplicateGroups)
        {
            if (!group.SpansMultipleBookRecords
                || group.Members.Select(value => value.Format).Distinct(StringComparer.Ordinal).Count() != 1)
                continue;
            if (!selected.TryGetValue(group.Id, out ExactDuplicateKeeperSelection? selection)
                || !group.Members.Contains(selection.RetainedMember))
            {
                issues.Add(new("BULK_EXACT.GROUP_SELECTION_MISSING", ExecutionIssueSeverity.Warning,
                    "An exact duplicate group had no current keeper selection and was skipped."));
                continue;
            }
            if (selection.Skip) continue;
            retainedRecords.Add(selection.RetainedMember.BookId);
            foreach (ExactBinaryDuplicateMember member in group.Members.Where(value => value != selection.RetainedMember))
            {
                BookFormat format = books[member.BookId].Formats.Single(value =>
                    value.Format == member.Format && Normalize(value.ExpectedRelativePath) == Normalize(member.ExpectedRelativePath));
                exactRemovals.TryAdd(new(member.BookId, member.Format), new(member.BookId, format));
                if (member.BookId == selection.RetainedMember.BookId) continue;
                if (!sourceTargets.TryGetValue(member.BookId, out HashSet<CalibreBookId>? targets))
                {
                    targets = [];
                    sourceTargets.Add(member.BookId, targets);
                }
                targets.Add(selection.RetainedMember.BookId);
            }
        }

        Dictionary<CalibreBookId, BookFormat[]> remaining = books.Values.ToDictionary(
            value => value.Id,
            value => value.Formats.Where(format => !exactRemovals.ContainsKey(new(value.Id, format.Format))).ToArray());
        Dictionary<FormatKey, FormatFileFingerprint?> targetInventory = [];
        foreach (CalibreBook book in books.Values)
            foreach (BookFormat format in remaining[book.Id])
                targetInventory[new(book.Id, format.Format)] = format.Fingerprint;

        List<TransferOperation> transfers = [];
        HashSet<CalibreBookId> mergedSources = [];
        int skipped = 0;
        foreach ((CalibreBookId sourceId, HashSet<CalibreBookId> targets) in sourceTargets.OrderBy(value => value.Key.Value))
        {
            BookFormat[] sourceFormats = remaining[sourceId];
            if (sourceFormats.Length == 0) continue;
            if (retainedRecords.Contains(sourceId) || targets.Count != 1)
            {
                skipped++;
                continue;
            }
            CalibreBookId targetId = targets.Single();
            bool physicalFactsAvailable = sourceFormats.All(value =>
                value.FileStatus == FormatFileStatus.Present && value.Fingerprint is not null
                && value.Observation is not null && !string.IsNullOrWhiteSpace(value.ExpectedRelativePath));
            bool conflict = sourceFormats.Any(value =>
                targetInventory.TryGetValue(new(targetId, value.Format), out FormatFileFingerprint? existing)
                && existing != value.Fingerprint);
            if (!physicalFactsAvailable || conflict)
            {
                skipped++;
                continue;
            }
            foreach (BookFormat format in sourceFormats)
            {
                FormatKey targetKey = new(targetId, format.Format);
                bool needsAdd = !targetInventory.ContainsKey(targetKey);
                transfers.Add(new(sourceId, targetId, format, needsAdd));
                targetInventory[targetKey] = format.Fingerprint;
            }
            mergedSources.Add(sourceId);
        }

        Dictionary<FormatKey, FormatRemovalOperation> allRemovals = new(exactRemovals);
        foreach (TransferOperation transfer in transfers)
            allRemovals.TryAdd(new(transfer.SourceRecordId, transfer.SourceFormat.Format),
                new(transfer.SourceRecordId, transfer.SourceFormat));

        HashSet<CalibreBookId> affected = exactRemovals.Keys.Select(value => value.RecordId)
            .Concat(mergedSources).ToHashSet();
        CalibreBookId[] emptyRecords = affected.Where(recordId =>
                books[recordId].Formats.All(format => allRemovals.ContainsKey(new(recordId, format.Format))))
            .OrderBy(value => value.Value).ToArray();
        return new(transfers.ToArray(), allRemovals.Values
                .OrderBy(value => value.RecordId.Value)
                .ThenBy(value => value.Format.Format, StringComparer.Ordinal).ToArray(),
            emptyRecords, mergedSources, skipped);
    }

    private static string Normalize(string value) => value.Replace('\\', '/');

    private static ExecutionIssue Block(
        string code,
        string explanation,
        CalibreBookId? recordId = null,
        string? format = null) => new(code, ExecutionIssueSeverity.BlockingError,
        explanation, recordId, format);

    internal readonly record struct FormatKey(CalibreBookId RecordId, string Format);
    internal sealed record FormatRemovalOperation(CalibreBookId RecordId, BookFormat Format);
    internal sealed record TransferOperation(
        CalibreBookId SourceRecordId,
        CalibreBookId TargetRecordId,
        BookFormat SourceFormat,
        bool NeedsAdd);
    private sealed record WorkerPlannedOperation(
        CalibreMutationOperation Operation,
        FormatRemovalOperation? Removal);
    internal sealed record BulkPlan(
        IReadOnlyList<TransferOperation> Transfers,
        IReadOnlyList<FormatRemovalOperation> FormatRemovals,
        IReadOnlyList<CalibreBookId> RecordsToRemove,
        IReadOnlySet<CalibreBookId> MergedRecordIds,
        int SkippedRecordCount)
    {
        public int TotalOperations => Transfers.Count(value => value.NeedsAdd)
            + FormatRemovals.Count + RecordsToRemove.Count;
    }
}
