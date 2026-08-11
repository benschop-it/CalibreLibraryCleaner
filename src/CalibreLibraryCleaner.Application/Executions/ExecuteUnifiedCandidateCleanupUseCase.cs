using System.Diagnostics;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed partial class ExecuteUnifiedCandidateCleanupUseCase(
    ILibraryStateSession libraryState,
    ICalibreToolDiscovery toolDiscovery,
    ICalibreMutationWorkerFactory workerFactory,
    ILibraryMutationLease mutationLease,
    ICleanupExecutionIdGenerator executionIds,
    IClock clock,
    ILogger<ExecuteUnifiedCandidateCleanupUseCase>? logger = null) : IUnifiedCandidateCleanupExecutor
{
    private readonly ILogger<ExecuteUnifiedCandidateCleanupUseCase> _logger = logger
        ?? NullLogger<ExecuteUnifiedCandidateCleanupUseCase>.Instance;

    public async Task<UnifiedCandidateCleanupResult> ExecuteAsync(
        ExecuteUnifiedCandidateCleanupRequest request,
        IProgress<UnifiedCandidateCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        long started = Stopwatch.GetTimestamp();
        CleanupExecutionId executionId = executionIds.Create();
        List<ExecutionIssue> issues = [];
        if (!request.ExternalBackupConfirmed)
        {
            issues.Add(Block(
                "CANDIDATE.BACKUP_NOT_CONFIRMED",
                "Confirm that a complete external library backup exists before Candidate cleanup."));
            return Result(UnifiedCandidateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }

        LibraryState? initial = libraryState.GetCurrent(request.LibraryRoot);
        if (initial is null)
        {
            issues.Add(Block("CANDIDATE.STATE_UNAVAILABLE", "Candidate analysis state is unavailable."));
            return Result(UnifiedCandidateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }

        UnifiedCandidateCleanupPlan plan;
        try
        {
            plan = UnifiedCandidateCleanupPlanner.Build(
                initial,
                request.ExpectedGeneration,
                request.ExpectedRevision,
                request.GroupSelections,
                issues);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            issues.Add(Block("CANDIDATE.PLAN_INVALID", exception.Message));
            return Result(UnifiedCandidateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }

        if (plan.TotalOperations == 0)
        {
            LibraryStateSessionOutcome completed = await AdvanceCompletedAsync(request.LibraryRoot)
                .ConfigureAwait(false);
            if (!completed.IsSuccess)
            {
                issues.Add(Block(
                    "CANDIDATE.COMPLETION_PERSIST_FAILED",
                    completed.Explanation ?? "Candidate completion could not be persisted."));
                return Result(UnifiedCandidateCleanupState.PreflightFailed, 0, 0, 0, plan.SkippedGroupCount);
            }
            LogCleanupCompleted(_logger, executionId.ToString(), "NothingToDo", 0, 0, 0, 0,
                plan.SkippedGroupCount, ElapsedMilliseconds(started));
            return Result(UnifiedCandidateCleanupState.NothingToDo, 0, 0, 0, plan.SkippedGroupCount);
        }

        LogCleanupStarted(_logger, executionId.ToString(), plan.TotalOperations);
        CalibreToolDiscoveryResult discovery = await toolDiscovery.DiscoverAndProbeAsync(
            request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        issues.AddRange(discovery.Issues);
        if (!discovery.IsSuccess)
            return Result(UnifiedCandidateCleanupState.PreflightFailed, 0, 0, 0, plan.SkippedGroupCount);
        LibraryMutationLeaseAcquisition acquisition = await mutationLease.TryAcquireAsync(new(
            executionId.ToString(),
            LibraryMutationKind.Cleanup,
            request.LibraryRoot,
            initial.Snapshot.Identity.CalibreLibraryUuid,
            clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        issues.AddRange(acquisition.Issues);
        if (!acquisition.IsAcquired)
            return Result(UnifiedCandidateCleanupState.PreflightFailed, 0, 0, 0, plan.SkippedGroupCount);

        await using ILibraryMutationLeaseHandle lease = acquisition.Lease!;
        int completedOperations = 0;
        int transferred = 0;
        int removedFormats = 0;
        int removedRecords = 0;
        bool markerWritten = false;
        bool mutationStarted = false;
        try
        {
            CalibreMutationWorkerOpenResult worker = await workerFactory.TryOpenAsync(new(
                discovery.Tool!, request.LibraryRoot, initial.Snapshot.Identity.CalibreLibraryUuid),
                cancellationToken).ConfigureAwait(false);
            if (!worker.IsSuccess)
            {
                issues.Add(Block(
                    "CANDIDATE.WORKER_UNAVAILABLE",
                    $"The persistent Calibre worker could not start ({worker.FailureCode ?? "CALIBRE_WORKER_UNAVAILABLE"})."));
                return Result(UnifiedCandidateCleanupState.PreflightFailed, 0, 0, 0, plan.SkippedGroupCount);
            }

            await using ICalibreMutationWorkerSession session = worker.Session!;
            using IDisposable statePublication = libraryState.DeferStateChanged(request.LibraryRoot);
            WorkerPlannedOperation[] operations = BuildWorkerOperations(plan);
            string mutationRunId = executionId.ToString();
            LibraryState markerState = Current();
            LibraryStateSessionOutcome marker = await libraryState.BeginMutationBatchAsync(
                request.LibraryRoot,
                new(
                    mutationRunId,
                    markerState.GenerationId,
                    markerState.Revision,
                    operations.Length,
                    clock.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            if (!marker.IsSuccess)
            {
                issues.Add(Block(
                    "CANDIDATE.MUTATION_MARKER_FAILED",
                    "A durable Candidate cleanup marker could not be written before mutation."));
                return Result(UnifiedCandidateCleanupState.PreflightFailed,
                    transferred, removedFormats, removedRecords, plan.SkippedGroupCount);
            }
            markerWritten = true;

            int chunkNumber = 0;
            foreach (WorkerPlannedOperation[] chunk in operations.Chunk(
                         CalibreMutationChunkRequest.MaximumOperationCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lease.IsHeld) throw new InvalidOperationException("The Candidate cleanup lease was lost.");
                progress?.Report(new(
                    "Processing unified candidates through the persistent Calibre worker.",
                    completedOperations,
                    operations.Length));
                CalibreMutationChunkResult workerResult = await session.ExecuteChunkAsync(
                    new($"{executionId}:{++chunkNumber}", chunk.Select(value => value.Operation).ToArray()),
                    CancellationToken.None).ConfigureAwait(false);
                mutationStarted |= workerResult.MutationStarted;
                if (!workerResult.IsSuccess)
                {
                    await MarkUncertainAsync(
                        "CANDIDATE.WORKER_CHUNK_FAILED",
                        "The persistent Calibre worker did not complete a Candidate cleanup chunk unambiguously.")
                        .ConfigureAwait(false);
                    issues.Add(Block(
                        "CANDIDATE.WORKER_CHUNK_FAILED",
                        "The persistent Calibre worker could not complete Candidate cleanup."));
                    return Result(UnifiedCandidateCleanupState.PartiallyCompleted,
                        transferred, removedFormats, removedRecords, plan.SkippedGroupCount);
                }

                LibraryState current = Current();
                LibraryStateRevision revision = current.Revision;
                DateTimeOffset appliedAt = AppliedAt(current);
                List<LibraryStateDelta> deltas = new(chunk.Length);
                foreach (WorkerPlannedOperation operation in chunk)
                {
                    deltas.Add(CreateDelta(current.GenerationId, revision, appliedAt, operation));
                    revision = revision.Next();
                }
                LibraryStateSessionOutcome applied = await libraryState.ApplyMutationBatchAsync(
                    request.LibraryRoot,
                    mutationRunId,
                    deltas,
                    completeMutationIntent: completedOperations + chunk.Length == operations.Length,
                    CancellationToken.None).ConfigureAwait(false);
                if (!applied.IsSuccess)
                {
                    issues.Add(Block(
                        "CANDIDATE.DELTA_COMMIT_FAILED",
                        "A Candidate cleanup worker chunk could not be committed to projected state."));
                    return Result(UnifiedCandidateCleanupState.PartiallyCompleted,
                        transferred, removedFormats, removedRecords, plan.SkippedGroupCount);
                }
                transferred += chunk.Count(value => value.Operation.Kind == CalibreMutationOperationKind.TransferFormat);
                removedFormats += chunk.Count(value => value.Operation.Kind == CalibreMutationOperationKind.RemoveFormat);
                removedRecords += chunk.Count(value => value.Operation.Kind == CalibreMutationOperationKind.RemoveRecord);
                completedOperations += chunk.Length;
            }

            LibraryStateSessionOutcome checkpoint = await libraryState.CheckpointAsync(
                request.LibraryRoot, CancellationToken.None).ConfigureAwait(false);
            if (!checkpoint.IsSuccess)
            {
                await MarkUncertainAsync(
                    "CANDIDATE.CHECKPOINT_FAILED",
                    "Candidate mutation completed but its authoritative checkpoint could not be persisted.")
                    .ConfigureAwait(false);
                issues.Add(Block("CANDIDATE.CHECKPOINT_FAILED", checkpoint.Explanation
                    ?? "Candidate cleanup checkpoint publication failed."));
                return Result(UnifiedCandidateCleanupState.PartiallyCompleted,
                    transferred, removedFormats, removedRecords, plan.SkippedGroupCount);
            }
            LibraryStateSessionOutcome completed = await AdvanceCompletedAsync(request.LibraryRoot)
                .ConfigureAwait(false);
            if (!completed.IsSuccess)
            {
                await MarkUncertainAsync(
                    "CANDIDATE.COMPLETION_PERSIST_FAILED",
                    "Candidate mutation completed but its workflow completion could not be persisted.")
                    .ConfigureAwait(false);
                issues.Add(Block("CANDIDATE.COMPLETION_PERSIST_FAILED", completed.Explanation
                    ?? "Candidate completion publication failed."));
                return Result(UnifiedCandidateCleanupState.PartiallyCompleted,
                    transferred, removedFormats, removedRecords, plan.SkippedGroupCount);
            }
            progress?.Report(new("Candidate cleanup completed.", operations.Length, operations.Length));
            LogCleanupCompleted(_logger, executionId.ToString(), "Completed", operations.Length,
                transferred, removedFormats, removedRecords, plan.SkippedGroupCount,
                ElapsedMilliseconds(started));
            return Result(UnifiedCandidateCleanupState.Completed,
                transferred, removedFormats, removedRecords, plan.SkippedGroupCount);
        }
        catch (OperationCanceledException)
        {
            if (markerWritten)
                await MarkUncertainAsync(
                    "CANDIDATE.CANCELLED_AFTER_MARKER",
                    "Candidate cleanup was canceled after its durable mutation marker was written.")
                    .ConfigureAwait(false);
            return Result(markerWritten
                    ? UnifiedCandidateCleanupState.PartiallyCompleted
                    : UnifiedCandidateCleanupState.Cancelled,
                transferred, removedFormats, removedRecords, plan.SkippedGroupCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidOperationException or ArgumentException)
        {
            if (markerWritten || mutationStarted)
                await MarkUncertainAsync("CANDIDATE.UNEXPECTED_FAILURE", exception.Message)
                    .ConfigureAwait(false);
            LogMutationFailure(_logger, executionId.ToString(), "CANDIDATE.UNEXPECTED_FAILURE", exception);
            issues.Add(Block("CANDIDATE.EXECUTION_FAILED", exception.Message));
            return Result(markerWritten || mutationStarted
                    ? UnifiedCandidateCleanupState.PartiallyCompleted
                    : UnifiedCandidateCleanupState.PreflightFailed,
                transferred, removedFormats, removedRecords, plan.SkippedGroupCount);
        }

        LibraryState Current() => libraryState.GetCurrent(request.LibraryRoot)
            ?? throw new InvalidOperationException("Authoritative Candidate state disappeared.");

        DateTimeOffset AppliedAt(LibraryState state)
        {
            DateTimeOffset now = clock.GetUtcNow().ToUniversalTime();
            return now < state.ProjectedAtUtc ? state.ProjectedAtUtc : now;
        }

        Task<LibraryStateSessionOutcome> MarkUncertainAsync(string code, string explanation) =>
            libraryState.MarkUncertainAsync(
                request.LibraryRoot,
                new(code, explanation, clock.GetUtcNow()),
                CancellationToken.None);

        async Task<LibraryStateSessionOutcome> AdvanceCompletedAsync(string root)
        {
            LibraryState state = libraryState.GetCurrent(root)
                ?? throw new InvalidOperationException("Authoritative Candidate state disappeared.");
            DateTimeOffset publishedAt = clock.GetUtcNow().ToUniversalTime();
            if (publishedAt < state.ProjectedAtUtc) publishedAt = state.ProjectedAtUtc;
            return await libraryState.AdvanceWorkflowAsync(
                root, LibraryWorkflowPhase.Completed, publishedAt, CancellationToken.None)
                .ConfigureAwait(false);
        }

        UnifiedCandidateCleanupResult Result(
            UnifiedCandidateCleanupState state,
            int transferCount,
            int formatCount,
            int recordCount,
            int skippedCount) => new(
            executionId,
            state,
            transferCount,
            formatCount,
            recordCount,
            skippedCount,
            issues.ToArray());
    }

    private static WorkerPlannedOperation[] BuildWorkerOperations(UnifiedCandidateCleanupPlan plan) =>
    [
        .. plan.Transfers.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.TransferFormat(
                $"candidate-add:{value.TargetRecordId.Value}:{value.SourceFormat.Format}",
                value.SourceRecordId,
                value.TargetRecordId,
                value.SourceFormat.Format,
                value.SourceFormat.Fingerprint!),
            null)),
        .. plan.FormatRemovals.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.RemoveFormat(
                $"candidate-remove-format:{value.RecordId.Value}:{value.Format.Format}",
                value.RecordId,
                value.Format.Format),
            value)),
        .. plan.RecordsToRemove.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.RemoveRecord($"candidate-remove-record:{value.Value}", value),
            null)),
    ];

    private static LibraryStateDelta CreateDelta(
        LibraryStateGenerationId generation,
        LibraryStateRevision revision,
        DateTimeOffset appliedAt,
        WorkerPlannedOperation planned) => planned.Operation.Kind switch
        {
            CalibreMutationOperationKind.TransferFormat => new AddOrReplaceFormatLibraryStateDelta(
                generation,
                revision,
                planned.Operation.OperationId,
                appliedAt,
                planned.Operation.TargetRecordId!.Value,
                planned.Operation.CanonicalFormat!,
                planned.Operation.ExpectedFingerprint!,
                null),
            CalibreMutationOperationKind.RemoveFormat => new RemoveFormatLibraryStateDelta(
                generation,
                revision,
                planned.Operation.OperationId,
                appliedAt,
                planned.Operation.RecordId,
                planned.Operation.CanonicalFormat!,
                planned.Removal!.Format.Fingerprint!),
            CalibreMutationOperationKind.RemoveRecord => new RemoveRecordLibraryStateDelta(
                generation,
                revision,
                planned.Operation.OperationId,
                appliedAt,
                planned.Operation.RecordId),
            _ => throw new InvalidOperationException("The worker returned an unsupported Candidate operation."),
        };

    private static ExecutionIssue Block(string code, string explanation) =>
        new(code, ExecutionIssueSeverity.BlockingError, explanation);

    [LoggerMessage(80, LogLevel.Information,
        "Unified Candidate cleanup {ExecutionId} started with {OperationCount} operations.")]
    private static partial void LogCleanupStarted(ILogger logger, string executionId, int operationCount);

    [LoggerMessage(81, LogLevel.Error,
        "Unified Candidate cleanup {ExecutionId} failed with {FailureCode}.")]
    private static partial void LogMutationFailure(
        ILogger logger,
        string executionId,
        string failureCode,
        Exception? exception);

    private static long ElapsedMilliseconds(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    [LoggerMessage(82, LogLevel.Information,
        "Unified Candidate cleanup {ExecutionId} finished. Outcome={Outcome}, Operations={OperationCount}, TransferredFormats={TransferredFormats}, RemovedFormats={RemovedFormats}, RemovedRecords={RemovedRecords}, SkippedGroups={SkippedGroups}, TotalMilliseconds={TotalMilliseconds}.")]
    private static partial void LogCleanupCompleted(
        ILogger logger,
        string executionId,
        string outcome,
        int operationCount,
        int transferredFormats,
        int removedFormats,
        int removedRecords,
        int skippedGroups,
        long totalMilliseconds);

    private sealed record WorkerPlannedOperation(
        CalibreMutationOperation Operation,
        UnifiedCandidateFormatRemoval? Removal);
}
