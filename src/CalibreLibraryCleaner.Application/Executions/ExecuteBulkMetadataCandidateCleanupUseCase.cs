using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed partial class ExecuteBulkMetadataCandidateCleanupUseCase(
    ILibraryStateSession libraryState,
    ICalibreToolDiscovery toolDiscovery,
    ICalibreMutationWorkerFactory workerFactory,
    ILibraryMutationLease mutationLease,
    ICleanupExecutionIdGenerator executionIds,
    IClock clock,
    ILogger<ExecuteBulkMetadataCandidateCleanupUseCase>? logger = null)
{
    private readonly ILogger<ExecuteBulkMetadataCandidateCleanupUseCase> _logger = logger
        ?? NullLogger<ExecuteBulkMetadataCandidateCleanupUseCase>.Instance;

    public async Task<BulkMetadataCandidateCleanupResult> ExecuteAsync(
        ExecuteBulkMetadataCandidateCleanupRequest request,
        IProgress<BulkMetadataCandidateCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CleanupExecutionId executionId = executionIds.Create();
        List<ExecutionIssue> issues = [];
        if (!request.ExternalBackupConfirmed)
        {
            issues.Add(Block("BULK_METADATA.BACKUP_NOT_CONFIRMED",
                "Confirm that a complete external library backup exists before processing metadata candidates."));
            LogPreflightFailure(_logger, executionId.ToString(), "BULK_METADATA.BACKUP_NOT_CONFIRMED");
            return Result(BulkMetadataCandidateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }

        LibraryState? initial = libraryState.GetCurrent(request.LibraryRoot);
        if (initial is null || !initial.IsAuthoritative)
        {
            issues.Add(Block("BULK_METADATA.STATE_UNAVAILABLE",
                "Run or load an authoritative scan before processing metadata candidates."));
            return Result(BulkMetadataCandidateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }

        MetadataCleanupPlan plan;
        try
        {
            plan = BuildPlan(initial.Snapshot, request.GroupSelections, issues);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            issues.Add(Block("BULK_METADATA.PLAN_INVALID", exception.Message));
            return Result(BulkMetadataCandidateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }

        if (plan.TotalOperations == 0)
            return Result(BulkMetadataCandidateCleanupState.NothingToDo, 0, 0, 0, plan.SkippedGroupCount);

        LogCleanupStarted(_logger, executionId.ToString(), plan.TotalOperations);
        CalibreToolDiscoveryResult discovery = await toolDiscovery.DiscoverAndProbeAsync(
            request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        issues.AddRange(discovery.Issues);
        if (!discovery.IsSuccess)
            return Result(BulkMetadataCandidateCleanupState.PreflightFailed, 0, 0, 0, plan.SkippedGroupCount);

        LibraryMutationLeaseAcquisition acquisition = await mutationLease.TryAcquireAsync(new(
            executionId.ToString(), LibraryMutationKind.Cleanup, request.LibraryRoot,
            initial.Snapshot.Identity.CalibreLibraryUuid, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        issues.AddRange(acquisition.Issues);
        if (!acquisition.IsAcquired)
            return Result(BulkMetadataCandidateCleanupState.PreflightFailed, 0, 0, 0, plan.SkippedGroupCount);

        await using ILibraryMutationLeaseHandle lease = acquisition.Lease!;
        int completed = 0;
        int transferredFormats = 0;
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
                string failureCode = worker.FailureCode ?? "CALIBRE_WORKER_UNAVAILABLE";
                LogPreflightFailure(_logger, executionId.ToString(), failureCode);
                issues.Add(Block("BULK_METADATA.WORKER_UNAVAILABLE",
                    $"The persistent Calibre worker could not start ({failureCode})."));
                return Result(BulkMetadataCandidateCleanupState.PreflightFailed, 0, 0, 0,
                    plan.SkippedGroupCount);
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
                LogPreflightFailure(_logger, executionId.ToString(), "BULK_METADATA.MUTATION_MARKER_FAILED");
                issues.Add(Block("BULK_METADATA.MUTATION_MARKER_FAILED",
                    "A durable cleanup-run marker could not be written before Calibre mutation."));
                return Result(BulkMetadataCandidateCleanupState.PreflightFailed,
                    transferredFormats, removedFormats, removedRecords, plan.SkippedGroupCount);
            }

            int chunkNumber = 0;
            foreach (WorkerPlannedOperation[] chunk in operations.Chunk(CalibreMutationChunkRequest.MaximumOperationCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lease.IsHeld) throw new InvalidOperationException("The cleanup lease was lost.");
                progress?.Report(new("Processing metadata candidates through the persistent Calibre worker.",
                    completed, plan.TotalOperations));
                string chunkId = $"{executionId}:{++chunkNumber}";
                CalibreMutationOperation[] chunkOperations = chunk.Select(value => value.Operation).ToArray();
                CalibreMutationChunkResult workerResult = await session.ExecuteChunkAsync(
                    new(chunkId, chunkOperations), CancellationToken.None).ConfigureAwait(false);
                mutationStarted |= workerResult.MutationStarted;
                if (!workerResult.IsSuccess)
                {
                    string failureCode = workerResult.FailureCode ?? "BULK_METADATA.WORKER_CHUNK_FAILED";
                    LogMutationFailure(_logger, executionId.ToString(), failureCode, null);
                    await MarkUncertainAsync("BULK_METADATA.WORKER_CHUNK_FAILED",
                        "The persistent Calibre worker did not complete a metadata cleanup chunk unambiguously.")
                        .ConfigureAwait(false);
                    issues.Add(Block("BULK_METADATA.WORKER_CHUNK_FAILED",
                        "The persistent Calibre worker could not complete metadata candidate cleanup."));
                    return Result(BulkMetadataCandidateCleanupState.PartiallyCompleted,
                        transferredFormats, removedFormats, removedRecords, plan.SkippedGroupCount);
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
                    LogMutationFailure(_logger, executionId.ToString(), "BULK_METADATA.DELTA_COMMIT_FAILED", null);
                    issues.Add(Block("BULK_METADATA.DELTA_COMMIT_FAILED",
                        "A metadata cleanup worker chunk could not be committed to projected state."));
                    return Result(BulkMetadataCandidateCleanupState.PartiallyCompleted,
                        transferredFormats, removedFormats, removedRecords, plan.SkippedGroupCount);
                }

                transferredFormats += chunk.Count(value => value.Operation.Kind == CalibreMutationOperationKind.TransferFormat);
                removedFormats += chunk.Count(value => value.Operation.Kind == CalibreMutationOperationKind.RemoveFormat);
                removedRecords += chunk.Count(value => value.Operation.Kind == CalibreMutationOperationKind.RemoveRecord);
                completed += chunk.Length;
            }

            LibraryStateSessionOutcome checkpoint = await libraryState.CheckpointAsync(
                request.LibraryRoot, CancellationToken.None).ConfigureAwait(false);
            if (!checkpoint.IsSuccess)
            {
                issues.Add(new("BULK_METADATA.CHECKPOINT_FAILED", ExecutionIssueSeverity.Warning,
                    "Metadata cleanup completed, but projected state checkpoint compaction was deferred."));
            }

            progress?.Report(new("Metadata candidate cleanup completed.", plan.TotalOperations, plan.TotalOperations));
            LogCleanupCompleted(_logger, executionId.ToString(), plan.TotalOperations);
            return Result(BulkMetadataCandidateCleanupState.Completed,
                transferredFormats, removedFormats, removedRecords, plan.SkippedGroupCount);
        }
        catch (OperationCanceledException)
        {
            if (mutationStarted)
            {
                await MarkUncertainAsync("BULK_METADATA.CANCELLED_AFTER_MUTATION",
                    "Metadata cleanup was canceled after mutation started.").ConfigureAwait(false);
            }
            return Result(mutationStarted ? BulkMetadataCandidateCleanupState.PartiallyCompleted
                : BulkMetadataCandidateCleanupState.Cancelled,
                transferredFormats, removedFormats, removedRecords, plan.SkippedGroupCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidOperationException or ArgumentException)
        {
            if (mutationStarted)
            {
                await MarkUncertainAsync("BULK_METADATA.UNEXPECTED_FAILURE", exception.Message)
                    .ConfigureAwait(false);
            }
            LogMutationFailure(_logger, executionId.ToString(), "BULK_METADATA.UNEXPECTED_FAILURE", exception);
            issues.Add(Block("BULK_METADATA.EXECUTION_FAILED", exception.Message));
            return Result(mutationStarted ? BulkMetadataCandidateCleanupState.PartiallyCompleted
                : BulkMetadataCandidateCleanupState.PreflightFailed,
                transferredFormats, removedFormats, removedRecords, plan.SkippedGroupCount);
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

        BulkMetadataCandidateCleanupResult Result(
            BulkMetadataCandidateCleanupState state,
            int transferCount,
            int formatCount,
            int recordCount,
            int skippedCount) => new(executionId, state, transferCount, formatCount,
            recordCount, skippedCount, issues.ToArray());
    }

    internal static MetadataCleanupPlan BuildPlan(
        LibrarySnapshot snapshot,
        IReadOnlyList<MetadataCandidateCleanupSelection> selections,
        List<ExecutionIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selections);
        Dictionary<ExactMetadataDuplicateGroupId, MetadataCandidateCleanupSelection> selected = selections
            .GroupBy(value => value.GroupId)
            .ToDictionary(value => value.Key, value => value.Single());
        Dictionary<ExactMetadataDuplicateGroupId, ExactMetadataDuplicateGroup> groups =
            snapshot.ExactMetadataDuplicateGroups.ToDictionary(value => value.Id);
        Dictionary<ExactMetadataDuplicateGroupId, ConsolidationRecommendation> recommendations =
            snapshot.ConsolidationRecommendations.ToDictionary(value => value.GroupId);
        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books.ToDictionary(value => value.Id);
        List<TransferOperation> transfers = [];
        List<FormatRemovalOperation> removals = [];
        List<CalibreBookId> records = [];
        int skipped = 0;

        foreach (MetadataCandidateCleanupSelection selection in selections.OrderBy(value => value.GroupId.Value, StringComparer.Ordinal))
        {
            if (selection.Skip || selection.KeeperBookId is null)
            {
                skipped++;
                continue;
            }
            if (!groups.TryGetValue(selection.GroupId, out ExactMetadataDuplicateGroup? group)
                || !group.Members.Contains(selection.KeeperBookId.Value)
                || !recommendations.TryGetValue(selection.GroupId, out ConsolidationRecommendation? recommendation))
            {
                issues.Add(new("BULK_METADATA.GROUP_SELECTION_INVALID", ExecutionIssueSeverity.Warning,
                    "A metadata candidate group had no current keeper selection and was skipped."));
                skipped++;
                continue;
            }

            CalibreBookId keeperId = selection.KeeperBookId.Value;
            CalibreBook keeper = books[keeperId];
            CalibreBook[] sources = group.Members.Where(value => value != keeperId)
                .Select(value => books[value]).OrderBy(value => value.Id.Value).ToArray();
            if (group.Members.SelectMany(value => books[value].Formats)
                .Any(value => value.FileStatus != FormatFileStatus.Present || value.Fingerprint is null))
            {
                issues.Add(new("BULK_METADATA.PHYSICAL_FACTS_INCOMPLETE", ExecutionIssueSeverity.Warning,
                    "A metadata candidate group has missing or unverified formats and was skipped."));
                skipped++;
                continue;
            }

            HashSet<string> keeperFormats = keeper.Formats.Select(value => value.Format)
                .ToHashSet(StringComparer.Ordinal);
            List<TransferOperation> groupTransfers = [];
            bool unresolved = false;
            foreach (string format in sources.SelectMany(value => value.Formats)
                         .Select(value => value.Format).Distinct(StringComparer.Ordinal)
                         .Where(value => !keeperFormats.Contains(value)).Order(StringComparer.Ordinal))
            {
                FormatSourceSelection? generated = recommendation.FormatSelections
                    .SingleOrDefault(value => value.Format == format);
                RecommendationFormatCandidate? source = generated is { ResolutionStatus: FormatResolutionStatus.Selected }
                    ? generated.ProposedSource
                    : null;
                if (source is null || source.BookId == keeperId || source.FileStatus != FormatFileStatus.Present
                    || source.Fingerprint is null || !group.Members.Contains(source.BookId))
                {
                    unresolved = true;
                    break;
                }
                BookFormat sourceFormat = books[source.BookId].Formats.Single(value => value.Format == format);
                groupTransfers.Add(new(source.BookId, keeperId, sourceFormat));
            }
            if (unresolved)
            {
                issues.Add(new("BULK_METADATA.COMPLEMENTARY_FORMAT_UNRESOLVED", ExecutionIssueSeverity.Warning,
                    "A complementary format has no generated source and the group was skipped."));
                skipped++;
                continue;
            }

            transfers.AddRange(groupTransfers);
            foreach (CalibreBook source in sources)
            {
                removals.AddRange(source.Formats.OrderBy(value => value.Format, StringComparer.Ordinal)
                    .Select(value => new FormatRemovalOperation(source.Id, value)));
                records.Add(source.Id);
            }
        }

        return new(
            transfers.OrderBy(value => value.TargetRecordId.Value)
                .ThenBy(value => value.SourceFormat.Format, StringComparer.Ordinal)
                .ThenBy(value => value.SourceRecordId.Value).ToArray(),
            removals.OrderBy(value => value.RecordId.Value)
                .ThenBy(value => value.Format.Format, StringComparer.Ordinal).ToArray(),
            records.Distinct().OrderBy(value => value.Value).ToArray(),
            skipped);
    }

    private static WorkerPlannedOperation[] BuildWorkerOperations(MetadataCleanupPlan plan) =>
    [
        .. plan.Transfers.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.TransferFormat(
                $"metadata-add:{value.TargetRecordId.Value}:{value.SourceFormat.Format}",
                value.SourceRecordId, value.TargetRecordId, value.SourceFormat.Format,
                value.SourceFormat.Fingerprint!), null)),
        .. plan.FormatRemovals.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.RemoveFormat(
                $"metadata-remove-format:{value.RecordId.Value}:{value.Format.Format}",
                value.RecordId, value.Format.Format), value)),
        .. plan.RecordsToRemove.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.RemoveRecord($"metadata-remove-record:{value.Value}", value), null)),
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

    [LoggerMessage(20, LogLevel.Information,
        "Metadata candidate cleanup {ExecutionId} started with {OperationCount} operations.")]
    private static partial void LogCleanupStarted(ILogger logger, string executionId, int operationCount);

    [LoggerMessage(21, LogLevel.Warning,
        "Metadata candidate cleanup {ExecutionId} failed preflight with {FailureCode}.")]
    private static partial void LogPreflightFailure(ILogger logger, string executionId, string failureCode);

    [LoggerMessage(22, LogLevel.Error,
        "Metadata candidate cleanup {ExecutionId} mutation failed with {FailureCode}.")]
    private static partial void LogMutationFailure(
        ILogger logger,
        string executionId,
        string failureCode,
        Exception? exception);

    [LoggerMessage(23, LogLevel.Information,
        "Metadata candidate cleanup {ExecutionId} completed {OperationCount} operations.")]
    private static partial void LogCleanupCompleted(ILogger logger, string executionId, int operationCount);

    internal sealed record MetadataCleanupPlan(
        IReadOnlyList<TransferOperation> Transfers,
        IReadOnlyList<FormatRemovalOperation> FormatRemovals,
        IReadOnlyList<CalibreBookId> RecordsToRemove,
        int SkippedGroupCount)
    {
        public int TotalOperations => Transfers.Count + FormatRemovals.Count + RecordsToRemove.Count;
    }

    internal sealed record TransferOperation(
        CalibreBookId SourceRecordId,
        CalibreBookId TargetRecordId,
        BookFormat SourceFormat);

    internal sealed record FormatRemovalOperation(CalibreBookId RecordId, BookFormat Format);

    private sealed record WorkerPlannedOperation(
        CalibreMutationOperation Operation,
        FormatRemovalOperation? Removal);
}
