using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed partial class ExecuteBulkExpandedCandidateCleanupUseCase(
    ILibraryStateSession libraryState,
    ICalibreToolDiscovery toolDiscovery,
    ICalibreMutationWorkerFactory workerFactory,
    ILibraryMutationLease mutationLease,
    ICleanupExecutionIdGenerator executionIds,
    IClock clock,
    ILogger<ExecuteBulkExpandedCandidateCleanupUseCase>? logger = null)
{
    private readonly ILogger<ExecuteBulkExpandedCandidateCleanupUseCase> _logger = logger
        ?? NullLogger<ExecuteBulkExpandedCandidateCleanupUseCase>.Instance;

    public async Task<BulkExpandedCandidateCleanupResult> ExecuteAsync(
        ExecuteBulkExpandedCandidateCleanupRequest request,
        IProgress<BulkExpandedCandidateCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CleanupExecutionId executionId = executionIds.Create();
        List<ExecutionIssue> issues = [];
        if (!request.ExternalBackupConfirmed)
        {
            issues.Add(Block("BULK_EXPANDED.BACKUP_NOT_CONFIRMED",
                "Confirm that a complete external library backup exists before processing expanded candidates."));
            return Result(BulkExpandedCandidateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }

        LibraryState? initial = libraryState.GetCurrent(request.LibraryRoot);
        if (initial is null || !initial.IsAuthoritative)
        {
            issues.Add(Block("BULK_EXPANDED.STATE_UNAVAILABLE",
                "Run an explicit scan before processing expanded candidates."));
            return Result(BulkExpandedCandidateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }

        ExpandedCleanupPlan plan;
        try
        {
            plan = BuildPlan(initial.Snapshot, request.GroupSelections, issues);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            issues.Add(Block("BULK_EXPANDED.PLAN_INVALID", exception.Message));
            return Result(BulkExpandedCandidateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }
        if (plan.TotalOperations == 0)
            return Result(BulkExpandedCandidateCleanupState.NothingToDo, 0, 0, 0, plan.SkippedGroupCount);

        LogCleanupStarted(_logger, executionId.ToString(), plan.TotalOperations);
        CalibreToolDiscoveryResult discovery = await toolDiscovery.DiscoverAndProbeAsync(
            request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        issues.AddRange(discovery.Issues);
        if (!discovery.IsSuccess)
            return Result(BulkExpandedCandidateCleanupState.PreflightFailed, 0, 0, 0, plan.SkippedGroupCount);

        LibraryMutationLeaseAcquisition acquisition = await mutationLease.TryAcquireAsync(new(
            executionId.ToString(), LibraryMutationKind.Cleanup, request.LibraryRoot,
            initial.Snapshot.Identity.CalibreLibraryUuid, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        issues.AddRange(acquisition.Issues);
        if (!acquisition.IsAcquired)
            return Result(BulkExpandedCandidateCleanupState.PreflightFailed, 0, 0, 0, plan.SkippedGroupCount);

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
                issues.Add(Block("BULK_EXPANDED.WORKER_UNAVAILABLE",
                    $"The persistent Calibre worker could not start ({worker.FailureCode ?? "CALIBRE_WORKER_UNAVAILABLE"})."));
                return Result(BulkExpandedCandidateCleanupState.PreflightFailed, 0, 0, 0,
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
                issues.Add(Block("BULK_EXPANDED.MUTATION_MARKER_FAILED",
                    "A durable cleanup-run marker could not be written before Calibre mutation."));
                return Result(BulkExpandedCandidateCleanupState.PreflightFailed,
                    transferredFormats, removedFormats, removedRecords, plan.SkippedGroupCount);
            }

            int chunkNumber = 0;
            foreach (WorkerPlannedOperation[] chunk in operations.Chunk(
                CalibreMutationChunkRequest.MaximumOperationCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lease.IsHeld) throw new InvalidOperationException("The cleanup lease was lost.");
                progress?.Report(new(
                    "Processing expanded candidates through the persistent Calibre worker.",
                    completed,
                    plan.TotalOperations));
                CalibreMutationChunkResult workerResult = await session.ExecuteChunkAsync(
                    new($"{executionId}:{++chunkNumber}", chunk.Select(value => value.Operation).ToArray()),
                    CancellationToken.None).ConfigureAwait(false);
                mutationStarted |= workerResult.MutationStarted;
                if (!workerResult.IsSuccess)
                {
                    string failureCode = workerResult.FailureCode ?? "BULK_EXPANDED.WORKER_CHUNK_FAILED";
                    LogMutationFailure(_logger, executionId.ToString(), failureCode, null);
                    await MarkUncertainAsync("BULK_EXPANDED.WORKER_CHUNK_FAILED",
                        "The persistent Calibre worker did not complete an expanded cleanup chunk unambiguously.")
                        .ConfigureAwait(false);
                    issues.Add(Block("BULK_EXPANDED.WORKER_CHUNK_FAILED",
                        "The persistent Calibre worker could not complete expanded candidate cleanup."));
                    return Result(BulkExpandedCandidateCleanupState.PartiallyCompleted,
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
                    request.LibraryRoot,
                    mutationRunId,
                    deltas,
                    completeMutationIntent: completed + chunk.Length == operations.Length,
                    CancellationToken.None).ConfigureAwait(false);
                if (!applied.IsSuccess)
                {
                    LogMutationFailure(_logger, executionId.ToString(), "BULK_EXPANDED.DELTA_COMMIT_FAILED", null);
                    issues.Add(Block("BULK_EXPANDED.DELTA_COMMIT_FAILED",
                        "An expanded cleanup worker chunk could not be committed to projected state."));
                    return Result(BulkExpandedCandidateCleanupState.PartiallyCompleted,
                        transferredFormats, removedFormats, removedRecords, plan.SkippedGroupCount);
                }

                transferredFormats += chunk.Count(value =>
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
                issues.Add(new("BULK_EXPANDED.CHECKPOINT_FAILED", ExecutionIssueSeverity.Warning,
                    "Expanded cleanup completed, but projected state checkpoint compaction was deferred."));
            progress?.Report(new("Expanded candidate cleanup completed.",
                plan.TotalOperations, plan.TotalOperations));
            LogCleanupCompleted(_logger, executionId.ToString(), plan.TotalOperations);
            return Result(BulkExpandedCandidateCleanupState.Completed,
                transferredFormats, removedFormats, removedRecords, plan.SkippedGroupCount);
        }
        catch (OperationCanceledException)
        {
            if (mutationStarted)
                await MarkUncertainAsync("BULK_EXPANDED.CANCELLED_AFTER_MUTATION",
                    "Expanded cleanup was canceled after mutation started.").ConfigureAwait(false);
            return Result(mutationStarted
                    ? BulkExpandedCandidateCleanupState.PartiallyCompleted
                    : BulkExpandedCandidateCleanupState.Cancelled,
                transferredFormats, removedFormats, removedRecords, plan.SkippedGroupCount);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidOperationException or ArgumentException)
        {
            if (mutationStarted)
                await MarkUncertainAsync("BULK_EXPANDED.UNEXPECTED_FAILURE", exception.Message)
                    .ConfigureAwait(false);
            LogMutationFailure(_logger, executionId.ToString(), "BULK_EXPANDED.UNEXPECTED_FAILURE", exception);
            issues.Add(Block("BULK_EXPANDED.EXECUTION_FAILED", exception.Message));
            return Result(mutationStarted
                    ? BulkExpandedCandidateCleanupState.PartiallyCompleted
                    : BulkExpandedCandidateCleanupState.PreflightFailed,
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

        BulkExpandedCandidateCleanupResult Result(
            BulkExpandedCandidateCleanupState state,
            int transferCount,
            int formatCount,
            int recordCount,
            int skippedCount) => new(executionId, state, transferCount, formatCount,
            recordCount, skippedCount, issues.ToArray());
    }

    internal static ExpandedCleanupPlan BuildPlan(
        LibrarySnapshot snapshot,
        IReadOnlyList<ExpandedCandidateCleanupSelection> selections,
        List<ExecutionIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Select(value => value.GroupId).Distinct().Count() != selections.Count)
            throw new ArgumentException("Expanded cleanup selections must have unique group IDs.", nameof(selections));
        Dictionary<WorkLanguageCandidateGroupId, WorkLanguageCandidateGroup> groups =
            snapshot.WorkLanguageCandidateGroups.ToDictionary(value => value.Id);
        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books.ToDictionary(value => value.Id);
        List<TransferOperation> transfers = [];
        List<FormatRemovalOperation> removals = [];
        List<CalibreBookId> records = [];
        int skipped = 0;

        foreach (ExpandedCandidateCleanupSelection selection in selections
            .OrderBy(value => value.GroupId.Value, StringComparer.Ordinal))
        {
            if (selection.Skip || selection.KeeperBookId is null)
            {
                skipped++;
                continue;
            }
            if (!groups.TryGetValue(selection.GroupId, out WorkLanguageCandidateGroup? group)
                || !group.Members.Contains(selection.KeeperBookId.Value))
            {
                issues.Add(new("BULK_EXPANDED.GROUP_SELECTION_INVALID", ExecutionIssueSeverity.Warning,
                    "An expanded candidate group was stale or had no current keeper and was skipped."));
                skipped++;
                continue;
            }

            CalibreBook keeper = books[selection.KeeperBookId.Value];
            CalibreBook[] sources = group.Members.Where(value => value != keeper.Id)
                .Select(value => books[value]).OrderBy(value => value.Id.Value).ToArray();
            if (group.Members.SelectMany(value => books[value].Formats)
                .Any(value => value.FileStatus != FormatFileStatus.Present || value.Fingerprint is null))
            {
                issues.Add(new("BULK_EXPANDED.PHYSICAL_FACTS_INCOMPLETE", ExecutionIssueSeverity.Warning,
                    "An expanded candidate group has missing or unverified formats and was skipped."));
                skipped++;
                continue;
            }

            HashSet<string> keeperFormats = keeper.Formats.Select(value => value.Format)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string format in sources.SelectMany(value => value.Formats)
                .Select(value => value.Format).Distinct(StringComparer.Ordinal)
                .Where(value => !keeperFormats.Contains(value)).Order(StringComparer.Ordinal))
            {
                (CalibreBook Book, BookFormat Format) source = sources
                    .SelectMany(book => book.Formats.Where(value => value.Format == format)
                        .Select(value => (Book: book, Format: value)))
                    .OrderByDescending(value => AssessmentScore(snapshot, value.Book.Id, value.Format))
                    .ThenBy(value => value.Book.Id.Value)
                    .First();
                transfers.Add(new(source.Book.Id, keeper.Id, source.Format));
            }

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

    private static int AssessmentScore(LibrarySnapshot snapshot, CalibreBookId bookId, BookFormat format)
    {
        if (format.Format == "EPUB")
            return snapshot.EpubAssessments.FirstOrDefault(value =>
                value.CalibreBookId == bookId
                && string.Equals(value.ExpectedRelativePath, format.ExpectedRelativePath, StringComparison.Ordinal))
                ?.Score?.Value ?? -1;
        if (format.Format == "PDF")
            return snapshot.PdfAssessments.FirstOrDefault(value =>
                value.CalibreBookId == bookId
                && string.Equals(value.ExpectedRelativePath, format.ExpectedRelativePath, StringComparison.Ordinal))
                ?.Score?.Value ?? -1;
        return -1;
    }

    private static WorkerPlannedOperation[] BuildWorkerOperations(ExpandedCleanupPlan plan) =>
    [
        .. plan.Transfers.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.TransferFormat(
                $"expanded-add:{value.TargetRecordId.Value}:{value.SourceFormat.Format}",
                value.SourceRecordId, value.TargetRecordId, value.SourceFormat.Format,
                value.SourceFormat.Fingerprint!), null)),
        .. plan.FormatRemovals.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.RemoveFormat(
                $"expanded-remove-format:{value.RecordId.Value}:{value.Format.Format}",
                value.RecordId, value.Format.Format), value)),
        .. plan.RecordsToRemove.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.RemoveRecord($"expanded-remove-record:{value.Value}", value), null)),
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

    [LoggerMessage(40, LogLevel.Information,
        "Expanded candidate cleanup {ExecutionId} started with {OperationCount} operations.")]
    private static partial void LogCleanupStarted(ILogger logger, string executionId, int operationCount);

    [LoggerMessage(41, LogLevel.Error,
        "Expanded candidate cleanup {ExecutionId} mutation failed with {FailureCode}.")]
    private static partial void LogMutationFailure(
        ILogger logger,
        string executionId,
        string failureCode,
        Exception? exception);

    [LoggerMessage(42, LogLevel.Information,
        "Expanded candidate cleanup {ExecutionId} completed {OperationCount} operations.")]
    private static partial void LogCleanupCompleted(ILogger logger, string executionId, int operationCount);

    internal sealed record ExpandedCleanupPlan(
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
