using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed partial class ExecuteCompositeCleanupUseCase(
    ILibraryStateSession libraryState,
    ICalibreToolDiscovery toolDiscovery,
    ICalibreMutationWorkerFactory workerFactory,
    ILibraryMutationLease mutationLease,
    ICleanupExecutionIdGenerator executionIds,
    IClock clock,
    ILogger<ExecuteCompositeCleanupUseCase>? logger = null)
{
    private readonly ILogger<ExecuteCompositeCleanupUseCase> _logger = logger
        ?? NullLogger<ExecuteCompositeCleanupUseCase>.Instance;

    public CompositeCleanupBuildResult Build(BuildCompositeCleanupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        LibraryState? state = libraryState.GetCurrent(request.LibraryRoot);
        if (state is null || !state.IsAuthoritative)
            return new(null, [new(
                "COMPOSITE.STATE_UNAVAILABLE",
                CompositeCleanupCategory.CrossCategory,
                "Run or load authoritative state from one completed scan before Cleanup all.")]);
        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            state.Snapshot,
            request.ExactSelections,
            request.MetadataSelections,
            request.ExpandedSelections);
        LogPlanBuilt(
            _logger,
            request.ExactSelections.Count,
            request.ExactSelections.Count(value => value.Skip),
            request.MetadataSelections.Count,
            request.MetadataSelections.Count(value => value.Skip),
            request.ExpandedSelections.Count,
            request.ExpandedSelections.Count(value => value.Skip),
            state.Snapshot.WorkLanguageCandidateGroups.Count,
            state.Snapshot.WorkLanguageCandidateGroups.Count(value =>
                value.CleanupEligibility != Domain.Matching.WorkLanguageCleanupEligibility.ExplicitKeeperCleanup),
            plan.Summary.ExactSelectionCount,
            plan.Summary.MetadataSelectionCount,
            plan.Summary.ExpandedSelectionCount,
            plan.Summary.SkippedSelectionCount,
            plan.Summary.ReconciledKeeperCount,
            plan.Summary.TotalOperationCount,
            plan.Conflicts.Count);
        return new(plan.Conflicts.Count == 0 ? plan.Summary : null, plan.Conflicts);
    }

    public async Task<CompositeCleanupResult> ExecuteAsync(
        ExecuteCompositeCleanupRequest request,
        IProgress<CompositeCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CleanupExecutionId executionId = executionIds.Create();
        List<ExecutionIssue> issues = [];
        if (!request.ExternalBackupConfirmed)
        {
            issues.Add(Block("COMPOSITE.BACKUP_NOT_CONFIRMED",
                "Confirm that a complete external library backup exists before Cleanup all."));
            return Result(CompositeCleanupState.PreflightFailed, 0, 0, 0, []);
        }
        LibraryState? initial = libraryState.GetCurrent(request.LibraryRoot);
        if (initial is null || !initial.IsAuthoritative)
        {
            issues.Add(Block("COMPOSITE.STATE_UNAVAILABLE",
                "Run or load authoritative state from one completed scan before Cleanup all."));
            return Result(CompositeCleanupState.PreflightFailed, 0, 0, 0, []);
        }
        CompositeCleanupPlan plan = CompositeCleanupPlanner.Build(
            initial.Snapshot,
            request.ExactSelections,
            request.MetadataSelections,
            request.ExpandedSelections);
        if (plan.Conflicts.Count > 0)
            return Result(CompositeCleanupState.Conflict, 0, 0, 0, plan.Conflicts);
        if (plan.Summary.TotalOperationCount == 0)
            return Result(CompositeCleanupState.NothingToDo, 0, 0, 0, []);

        LogCleanupStarted(_logger, executionId.ToString(), plan.Summary.TotalOperationCount);
        CalibreToolDiscoveryResult discovery = await toolDiscovery.DiscoverAndProbeAsync(
            request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        issues.AddRange(discovery.Issues);
        if (!discovery.IsSuccess)
            return Result(CompositeCleanupState.PreflightFailed, 0, 0, 0, []);
        LibraryMutationLeaseAcquisition acquisition = await mutationLease.TryAcquireAsync(new(
            executionId.ToString(), LibraryMutationKind.Cleanup, request.LibraryRoot,
            initial.Snapshot.Identity.CalibreLibraryUuid, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        issues.AddRange(acquisition.Issues);
        if (!acquisition.IsAcquired)
            return Result(CompositeCleanupState.PreflightFailed, 0, 0, 0, []);

        await using ILibraryMutationLeaseHandle lease = acquisition.Lease!;
        int completed = 0;
        int transferred = 0;
        int removedFormats = 0;
        int removedRecords = 0;
        bool mutationStarted = false;
        try
        {
            CalibreMutationWorkerOpenResult worker = await workerFactory.TryOpenAsync(new(
                discovery.Tool!, request.LibraryRoot, initial.Snapshot.Identity.CalibreLibraryUuid),
                cancellationToken).ConfigureAwait(false);
            if (!worker.IsSuccess)
            {
                issues.Add(Block("COMPOSITE.WORKER_UNAVAILABLE",
                    $"The persistent Calibre worker could not start ({worker.FailureCode ?? "CALIBRE_WORKER_UNAVAILABLE"})."));
                return Result(CompositeCleanupState.PreflightFailed, 0, 0, 0, []);
            }

            await using ICalibreMutationWorkerSession session = worker.Session!;
            using IDisposable statePublication = libraryState.DeferStateChanged(request.LibraryRoot);
            WorkerPlannedOperation[] operations = BuildWorkerOperations(plan);
            string mutationRunId = executionId.ToString();
            LibraryState markerState = Current();
            LibraryStateSessionOutcome marker = await libraryState.BeginMutationBatchAsync(
                request.LibraryRoot,
                new LibraryStateMutationIntent(mutationRunId, markerState.GenerationId,
                    markerState.Revision, operations.Length, clock.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            if (!marker.IsSuccess)
            {
                issues.Add(Block("COMPOSITE.MUTATION_MARKER_FAILED",
                    "A durable Cleanup all marker could not be written before mutation."));
                return Result(CompositeCleanupState.PreflightFailed, 0, 0, 0, []);
            }

            int chunkNumber = 0;
            foreach (WorkerPlannedOperation[] chunk in operations.Chunk(
                CalibreMutationChunkRequest.MaximumOperationCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lease.IsHeld) throw new InvalidOperationException("The cleanup lease was lost.");
                progress?.Report(new("Processing reviewed cleanup through the persistent Calibre worker.",
                    completed, operations.Length));
                CalibreMutationChunkResult workerResult = await session.ExecuteChunkAsync(
                    new($"{executionId}:{++chunkNumber}", chunk.Select(value => value.Operation).ToArray()),
                    CancellationToken.None).ConfigureAwait(false);
                mutationStarted |= workerResult.MutationStarted;
                if (!workerResult.IsSuccess)
                {
                    string failureCode = workerResult.FailureCode ?? "COMPOSITE.WORKER_CHUNK_FAILED";
                    LogMutationFailure(_logger, executionId.ToString(), failureCode, null);
                    await MarkUncertainAsync("COMPOSITE.WORKER_CHUNK_FAILED",
                        "The persistent Calibre worker did not complete a Cleanup all chunk unambiguously.")
                        .ConfigureAwait(false);
                    issues.Add(Block("COMPOSITE.WORKER_CHUNK_FAILED",
                        "The persistent Calibre worker could not complete Cleanup all."));
                    return Result(CompositeCleanupState.PartiallyCompleted,
                        transferred, removedFormats, removedRecords, []);
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
                    completeMutationIntent: completed + chunk.Length == operations.Length,
                    CancellationToken.None).ConfigureAwait(false);
                if (!applied.IsSuccess)
                {
                    LogMutationFailure(_logger, executionId.ToString(), "COMPOSITE.DELTA_COMMIT_FAILED", null);
                    issues.Add(Block("COMPOSITE.DELTA_COMMIT_FAILED",
                        "A Cleanup all worker chunk could not be committed to projected state."));
                    return Result(CompositeCleanupState.PartiallyCompleted,
                        transferred, removedFormats, removedRecords, []);
                }
                transferred += chunk.Count(value =>
                    value.Operation.Kind == CalibreMutationOperationKind.TransferFormat);
                removedFormats += chunk.Count(value =>
                    value.Operation.Kind == CalibreMutationOperationKind.RemoveFormat);
                removedRecords += chunk.Count(value =>
                    value.Operation.Kind == CalibreMutationOperationKind.RemoveRecord);
                completed += chunk.Length;
            }

            LibraryStateSessionOutcome checkpoint = await libraryState.CheckpointAsync(
                request.LibraryRoot, CancellationToken.None).ConfigureAwait(false);
            if (!checkpoint.IsSuccess)
                issues.Add(new("COMPOSITE.CHECKPOINT_FAILED", ExecutionIssueSeverity.Warning,
                    "Cleanup all completed, but projected state checkpoint compaction was deferred."));
            progress?.Report(new("Cleanup all completed.", operations.Length, operations.Length));
            LogCleanupCompleted(_logger, executionId.ToString(), operations.Length);
            return Result(CompositeCleanupState.Completed,
                transferred, removedFormats, removedRecords, []);
        }
        catch (OperationCanceledException)
        {
            if (mutationStarted)
                await MarkUncertainAsync("COMPOSITE.CANCELLED_AFTER_MUTATION",
                    "Cleanup all was canceled after mutation started.").ConfigureAwait(false);
            return Result(mutationStarted
                    ? CompositeCleanupState.PartiallyCompleted
                    : CompositeCleanupState.Cancelled,
                transferred, removedFormats, removedRecords, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidOperationException or ArgumentException)
        {
            if (mutationStarted)
                await MarkUncertainAsync("COMPOSITE.UNEXPECTED_FAILURE", exception.Message)
                    .ConfigureAwait(false);
            LogMutationFailure(_logger, executionId.ToString(), "COMPOSITE.UNEXPECTED_FAILURE", exception);
            issues.Add(Block("COMPOSITE.EXECUTION_FAILED", exception.Message));
            return Result(mutationStarted
                    ? CompositeCleanupState.PartiallyCompleted
                    : CompositeCleanupState.PreflightFailed,
                transferred, removedFormats, removedRecords, []);
        }

        LibraryState Current() => libraryState.GetCurrent(request.LibraryRoot)
            ?? throw new InvalidOperationException("Authoritative projected state disappeared.");

        DateTimeOffset AppliedAt(LibraryState current)
        {
            DateTimeOffset now = clock.GetUtcNow().ToUniversalTime();
            return now < current.ProjectedAtUtc ? current.ProjectedAtUtc : now;
        }

        Task<LibraryStateSessionOutcome> MarkUncertainAsync(string code, string explanation) =>
            libraryState.MarkUncertainAsync(request.LibraryRoot,
                new(code, explanation, clock.GetUtcNow()), CancellationToken.None);

        CompositeCleanupResult Result(
            CompositeCleanupState state,
            int transferCount,
            int formatCount,
            int recordCount,
            IReadOnlyList<CompositeCleanupConflict> conflicts) => new(
            executionId, state, transferCount, formatCount, recordCount,
            conflicts, issues.ToArray());
    }

    private static WorkerPlannedOperation[] BuildWorkerOperations(CompositeCleanupPlan plan) =>
    [
        .. plan.Transfers.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.TransferFormat(
                $"composite-add:{value.TargetRecordId.Value}:{value.SourceFormat.Format}",
                value.SourceRecordId, value.TargetRecordId, value.SourceFormat.Format,
                value.SourceFormat.Fingerprint!), null)),
        .. plan.FormatRemovals.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.RemoveFormat(
                $"composite-remove-format:{value.RecordId.Value}:{value.Format.Format}",
                value.RecordId, value.Format.Format), value)),
        .. plan.RecordsToRemove.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.RemoveRecord($"composite-remove-record:{value.Value}", value), null)),
    ];

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

    private static ExecutionIssue Block(string code, string explanation) =>
        new(code, ExecutionIssueSeverity.BlockingError, explanation);

    [LoggerMessage(60, LogLevel.Information,
        "Cleanup all {ExecutionId} started with {OperationCount} operations.")]
    private static partial void LogCleanupStarted(ILogger logger, string executionId, int operationCount);

    [LoggerMessage(61, LogLevel.Error,
        "Cleanup all {ExecutionId} mutation failed with {FailureCode}.")]
    private static partial void LogMutationFailure(
        ILogger logger, string executionId, string failureCode, Exception? exception);

    [LoggerMessage(62, LogLevel.Information,
        "Cleanup all {ExecutionId} completed {OperationCount} operations.")]
    private static partial void LogCleanupCompleted(ILogger logger, string executionId, int operationCount);

    [LoggerMessage(63, LogLevel.Information,
        "Cleanup all plan built. Requested exact={RequestedExact} (user skipped={SkippedExact}), metadata={RequestedMetadata} (user skipped={SkippedMetadata}), expanded={RequestedExpanded} (user skipped={SkippedExpanded}); available expanded={AvailableExpanded}, expanded to be reviewed={ToBeReviewedExpanded}; planned exact={PlannedExact}, metadata={PlannedMetadata}, expanded={PlannedExpanded}, preflight skipped={PreflightSkipped}, generated keepers reconciled={ReconciledKeepers}, operations={OperationCount}, conflicts={ConflictCount}.")]
    private static partial void LogPlanBuilt(
        ILogger logger,
        int requestedExact,
        int skippedExact,
        int requestedMetadata,
        int skippedMetadata,
        int requestedExpanded,
        int skippedExpanded,
        int availableExpanded,
        int toBeReviewedExpanded,
        int plannedExact,
        int plannedMetadata,
        int plannedExpanded,
        int preflightSkipped,
        int reconciledKeepers,
        int operationCount,
        int conflictCount);

    private sealed record WorkerPlannedOperation(
        CalibreMutationOperation Operation,
        CompositeFormatRemovalIntent? Removal);
}
