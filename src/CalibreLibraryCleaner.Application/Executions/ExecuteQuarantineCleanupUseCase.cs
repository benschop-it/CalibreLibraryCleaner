using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed partial class ExecuteQuarantineCleanupUseCase(
    ILibraryStateSession libraryState,
    ICalibreToolDiscovery toolDiscovery,
    ICalibreMutationWorkerFactory workerFactory,
    ILibraryMutationLease mutationLease,
    ICleanupExecutionIdGenerator executionIds,
    IClock clock,
    ILogger<ExecuteQuarantineCleanupUseCase>? logger = null) : IQuarantineCleanupExecutor
{
    private readonly ILogger<ExecuteQuarantineCleanupUseCase> _logger = logger
        ?? NullLogger<ExecuteQuarantineCleanupUseCase>.Instance;

    public async Task<QuarantineCleanupResult> ExecuteAsync(
        ExecuteQuarantineCleanupRequest request,
        IProgress<QuarantineCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CleanupExecutionId executionId = executionIds.Create();
        List<ExecutionIssue> issues = [];
        int copied = 0;
        int removed = 0;
        string? destinationUuid = null;
        if (!request.ExternalBackupsConfirmed)
            return Fail("QUARANTINE.BACKUP_NOT_CONFIRMED",
                "Confirm complete external backups of the source and any existing destination library.");
        LibraryState? initial = libraryState.GetCurrent(request.LibraryRoot);
        QuarantineReviewSubject[] selected;
        try
        {
            selected = ValidateAndSelect(initial, request);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Fail("QUARANTINE.REVIEW_STALE", exception.Message);
        }
        if (selected.Length == 0)
            return Result(QuarantineCleanupState.NothingToDo, 0, 0, null);

        CalibreToolDiscoveryResult discovery = await toolDiscovery.DiscoverAndProbeAsync(
            request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        issues.AddRange(discovery.Issues);
        if (!discovery.IsSuccess)
            return Result(QuarantineCleanupState.PreflightFailed, 0, 0, null);
        LibraryMutationLeaseAcquisition acquisition = await mutationLease.TryAcquireAsync(new(
            executionId.ToString(),
            LibraryMutationKind.Cleanup,
            request.LibraryRoot,
            initial!.Snapshot.Identity.CalibreLibraryUuid,
            clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        issues.AddRange(acquisition.Issues);
        if (!acquisition.IsAcquired)
            return Result(QuarantineCleanupState.PreflightFailed, 0, 0, null);

        await using ILibraryMutationLeaseHandle lease = acquisition.Lease!;
        bool markerWritten = false;
        bool mutationStarted = false;
        try
        {
            string mutationRunId = executionId.ToString();
            LibraryStateSessionOutcome marker = await libraryState.BeginMutationBatchAsync(
                request.LibraryRoot,
                new(
                    mutationRunId,
                    initial.GenerationId,
                    initial.Revision,
                    selected.Length,
                    clock.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            if (!marker.IsSuccess)
                return Fail("QUARANTINE.MUTATION_MARKER_FAILED",
                    marker.Explanation ?? "The quarantine mutation marker could not be persisted.");
            markerWritten = true;

            QuarantineDestinationIdentity destination = request.Review.DestinationIdentity!;
            CalibreMutationWorkerOpenResult opened = await workerFactory.TryOpenAsync(new(
                discovery.Tool!,
                request.LibraryRoot,
                initial.Snapshot.Identity.CalibreLibraryUuid,
                QuarantineDestinationRoot: request.Review.DestinationRoot,
                ExpectedQuarantineLibraryUuid: destination.LibraryUuid),
                cancellationToken).ConfigureAwait(false);
            if (!opened.IsSuccess
                || opened.QuarantineLibraryExisted != destination.Exists
                || destination.Exists && opened.QuarantineLibraryUuid != destination.LibraryUuid)
            {
                await MarkUncertainAsync("QUARANTINE.WORKER_UNAVAILABLE",
                    "The fixed worker could not bind the reviewed destination library.").ConfigureAwait(false);
                return Fail("QUARANTINE.WORKER_UNAVAILABLE",
                    $"The fixed Calibre worker could not start ({opened.FailureCode ?? "destination identity mismatch"}).",
                    QuarantineCleanupState.PartiallyCompleted);
            }
            destinationUuid = opened.QuarantineLibraryUuid;
            LogStarted(_logger, executionId.ToString(), selected.Length);
            await using ICalibreMutationWorkerSession session = opened.Session!;
            using IDisposable statePublication = libraryState.DeferStateChanged(request.LibraryRoot);
            CalibreMutationOperation[] copies = selected.Select(subject =>
                CalibreMutationOperation.CopyRecordToQuarantine(
                    $"{executionId}:copy:{subject.BookId.Value}",
                    subject.BookId,
                    Expectation(subject))).ToArray();
            int chunkNumber = 0;
            foreach (CalibreMutationOperation[] chunk in copies.Chunk(
                         CalibreMutationChunkRequest.MaximumOperationCount))
            {
                if (!lease.IsHeld) throw new InvalidOperationException("The quarantine mutation lease was lost.");
                progress?.Report(new("Copying and verifying quarantine records.", copied, selected.Length * 2));
                CalibreMutationChunkResult result = await session.ExecuteChunkAsync(
                    new($"{executionId}:copy:{++chunkNumber}", chunk),
                    CancellationToken.None).ConfigureAwait(false);
                mutationStarted |= result.MutationStarted;
                if (!result.IsSuccess
                    || result.OperationResults.Any(value => value.QuarantineRecordId is null)
                    || result.OperationResults.Select(value => value.QuarantineRecordId).Distinct().Count()
                        != result.OperationResults.Count)
                {
                    await MarkUncertainAsync("QUARANTINE.COPY_FAILED",
                        "A destination record copy was failed or ambiguous.").ConfigureAwait(false);
                    return Fail("QUARANTINE.COPY_FAILED",
                        $"Destination copy stopped ({FailureCode(result)}); no source deletion began.",
                        QuarantineCleanupState.PartiallyCompleted);
                }
                copied += chunk.Length;
            }

            for (int index = 0; index < selected.Length; index++)
            {
                if (!lease.IsHeld) throw new InvalidOperationException("The quarantine mutation lease was lost.");
                QuarantineReviewSubject subject = selected[index];
                progress?.Report(new("Deleting verified source records.", copied + removed, selected.Length * 2));
                CalibreMutationOperation operation = CalibreMutationOperation.RemoveRecordWithContent(
                    $"{executionId}:delete:{subject.BookId.Value}",
                    subject.BookId,
                    Expectation(subject));
                CalibreMutationChunkResult result = await session.ExecuteChunkAsync(
                    new($"{executionId}:delete:{index + 1}", [operation]),
                    CancellationToken.None).ConfigureAwait(false);
                mutationStarted |= result.MutationStarted;
                if (!result.IsSuccess)
                {
                    await MarkUncertainAsync("QUARANTINE.SOURCE_DELETE_FAILED",
                        "A verified source deletion failed or was ambiguous.").ConfigureAwait(false);
                    return Fail("QUARANTINE.SOURCE_DELETE_FAILED",
                        $"Source deletion stopped ({FailureCode(result)}).",
                        QuarantineCleanupState.PartiallyCompleted);
                }

                LibraryState current = Current();
                LibraryStateSessionOutcome applied = await libraryState.ApplyMutationBatchAsync(
                    request.LibraryRoot,
                    mutationRunId,
                    [new RemoveRecordWithContentLibraryStateDelta(
                        current.GenerationId,
                        current.Revision,
                        operation.OperationId,
                        AppliedAt(current),
                        subject.BookId)],
                    completeMutationIntent: index + 1 == selected.Length,
                    CancellationToken.None).ConfigureAwait(false);
                if (!applied.IsSuccess)
                {
                    await MarkUncertainAsync("QUARANTINE.DELTA_COMMIT_FAILED",
                        "A verified source deletion could not be projected.").ConfigureAwait(false);
                    return Fail("QUARANTINE.DELTA_COMMIT_FAILED",
                        applied.Explanation ?? "A verified source deletion could not be projected.",
                        QuarantineCleanupState.PartiallyCompleted);
                }
                removed++;
            }

            LibraryStateSessionOutcome checkpoint = await libraryState.CheckpointAsync(
                request.LibraryRoot, CancellationToken.None).ConfigureAwait(false);
            if (!checkpoint.IsSuccess)
            {
                await MarkUncertainAsync("QUARANTINE.CHECKPOINT_FAILED",
                    "Quarantine completed but its source checkpoint could not be persisted.").ConfigureAwait(false);
                return Fail("QUARANTINE.CHECKPOINT_FAILED",
                    checkpoint.Explanation ?? "The source checkpoint could not be persisted.",
                    QuarantineCleanupState.PartiallyCompleted);
            }
            progress?.Report(new("Quarantine move completed.", selected.Length * 2, selected.Length * 2));
            LogCompleted(_logger, executionId.ToString(), copied, removed);
            return Result(QuarantineCleanupState.Completed, copied, removed, destinationUuid);
        }
        catch (OperationCanceledException)
        {
            if (markerWritten)
                await MarkUncertainAsync("QUARANTINE.CANCELLED_AFTER_MARKER",
                    "The quarantine run was canceled after its mutation marker was persisted.").ConfigureAwait(false);
            return Result(markerWritten
                    ? QuarantineCleanupState.PartiallyCompleted
                    : QuarantineCleanupState.Cancelled,
                copied, removed, destinationUuid);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidOperationException or ArgumentException)
        {
            if (markerWritten || mutationStarted)
                await MarkUncertainAsync("QUARANTINE.UNEXPECTED_FAILURE", exception.Message)
                    .ConfigureAwait(false);
            LogFailed(_logger, executionId.ToString(), exception.GetType().Name);
            return Fail("QUARANTINE.EXECUTION_FAILED", exception.Message,
                markerWritten || mutationStarted
                    ? QuarantineCleanupState.PartiallyCompleted
                    : QuarantineCleanupState.PreflightFailed);
        }

        LibraryState Current() => libraryState.GetCurrent(request.LibraryRoot)
            ?? throw new InvalidOperationException("Authoritative quarantine source state disappeared.");

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

        QuarantineCleanupResult Fail(
            string code,
            string explanation,
            QuarantineCleanupState state = QuarantineCleanupState.PreflightFailed)
        {
            issues.Add(Block(code, explanation));
            return Result(state, copied, removed, destinationUuid);
        }

        QuarantineCleanupResult Result(
            QuarantineCleanupState state,
            int copiedCount,
            int removedCount,
            string? uuid) => new(
            executionId,
            state,
            copiedCount,
            removedCount,
            uuid,
            issues.ToArray());
    }

    private static QuarantineReviewSubject[] ValidateAndSelect(
        LibraryState? state,
        ExecuteQuarantineCleanupRequest request)
    {
        QuarantineReviewWorkspace review = request.Review
            ?? throw new ArgumentException("A quarantine review is required.", nameof(request));
        if (state is null
            || !state.IsAuthoritative
            || !state.IsWorkflowCheckpointCurrent
            || state.WorkflowCheckpoint.Phase != LibraryWorkflowPhase.Completed
            || state.GenerationId != request.ExpectedGeneration
            || state.Revision != request.ExpectedRevision
            || review.GenerationId != state.GenerationId
            || review.Revision != state.Revision
            || review.DestinationIdentity is null
            || review.DispositionPolicyVersion != FinalRecordDispositionPolicy.PolicyVersion
            || !string.Equals(review.LibraryRoot, state.Snapshot.Identity.LibraryRoot,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The quarantine review is stale.");
        Dictionary<CalibreBookId, CalibreBook> books = state.Snapshot.Books.ToDictionary(value => value.Id);
        QuarantineReviewSubject[] selected = review.Subjects.Where(value => value.Include)
            .OrderBy(value => value.BookId.Value)
            .ToArray();
        foreach (QuarantineReviewSubject subject in selected)
        {
            if (!books.TryGetValue(subject.BookId, out CalibreBook? book)
                || subject.CurrentTitle != book.Title
                || subject.InputIdentity != FinalRecordInputIdentity.Create(book)
                || !subject.CurrentAuthors.SequenceEqual(book.Authors)
                || subject.Formats.Count != book.Formats.Count
                || book.Formats.Any(value => value.FileStatus != FormatFileStatus.Present
                    || value.Fingerprint is null
                    || subject.Formats.All(expected => expected.Format != value.Format
                        || expected.ExpectedRelativePath != value.ExpectedRelativePath
                        || expected.Fingerprint != value.Fingerprint)))
                throw new InvalidOperationException("A selected quarantine record changed after review.");
        }
        return selected;
    }

    private static CalibreQuarantineRecordExpectation Expectation(QuarantineReviewSubject subject) => new(
        subject.CurrentTitle,
        subject.CurrentAuthors.Select(value => value.Name),
        subject.CurrentAuthors.Select(value => value.SortName),
        subject.Formats.Select(value => new CalibreQuarantineFormatExpectation(
            value.Format,
            value.ExpectedRelativePath,
            value.Fingerprint ?? throw new InvalidOperationException(
                "A quarantine format lacks a verified fingerprint."))));

    private static string FailureCode(CalibreMutationChunkResult result) =>
        result.FailureCode
        ?? result.OperationResults.FirstOrDefault(value => !value.IsSuccess)?.FailureCode
        ?? "CALIBRE_WORKER_UNKNOWN";

    private static ExecutionIssue Block(string code, string explanation) => new(
        code,
        ExecutionIssueSeverity.BlockingError,
        explanation);

    [LoggerMessage(1, LogLevel.Information,
        "Quarantine run {ExecutionId} started for {RecordCount} records")]
    private static partial void LogStarted(ILogger logger, string executionId, int recordCount);

    [LoggerMessage(2, LogLevel.Information,
        "Quarantine run {ExecutionId} completed with {CopiedCount} copied and {RemovedCount} removed records")]
    private static partial void LogCompleted(
        ILogger logger,
        string executionId,
        int copiedCount,
        int removedCount);

    [LoggerMessage(3, LogLevel.Error,
        "Quarantine run {ExecutionId} failed with {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string executionId, string exceptionType);
}
