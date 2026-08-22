using System.Diagnostics;
using System.Globalization;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Domain.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed partial class ExecuteUnifiedCandidateCleanupUseCase(
    ILibraryStateSession libraryState,
    ICalibreToolDiscovery toolDiscovery,
    ICalibreMutationWorkerFactory workerFactory,
    ILibraryMutationLease mutationLease,
    ICleanupExecutionIdGenerator executionIds,
    IEditionCoverStager coverStager,
    IClock clock,
    ILogger<ExecuteUnifiedCandidateCleanupUseCase>? logger = null) : IUnifiedCandidateCleanupExecutor
{
    private static readonly HashSet<string> WritableLanguages = CultureInfo
        .GetCultures(CultureTypes.NeutralCultures)
        .SelectMany(culture => new[]
        {
            culture.TwoLetterISOLanguageName,
            culture.ThreeLetterISOLanguageName,
        })
        .Where(value => value.Length is 2 or 3)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        int updatedMetadataFields = 0;
        int skippedMetadataFields = 0;
        int omittedMetadataCovers = 0;
        int omittedMetadataAuthorFields = 0;
        Dictionary<string, int> metadataSkipReasons = new(StringComparer.Ordinal);
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

        bool candidateCleanup = initial.WorkflowCheckpoint.Phase == LibraryWorkflowPhase.CandidateAnalysisReady;
        bool metadataMutation = initial.WorkflowCheckpoint.Phase == LibraryWorkflowPhase.CandidateCleanupCompleted;
        bool authorNormalization = initial.WorkflowCheckpoint.Phase == LibraryWorkflowPhase.Completed
            && request.NormalizeAuthors;
        if (!candidateCleanup && !metadataMutation && !authorNormalization)
        {
            issues.Add(Block("CANDIDATE.PHASE_INVALID", "The cleanup request is not valid for the current workflow phase."));
            return Result(UnifiedCandidateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }

        UnifiedCandidateCleanupPlan plan;
        MetadataOperationTemplate[] metadataTemplates;
        try
        {
            if (candidateCleanup)
            {
                if (request.MetadataReview is not null)
                    throw new InvalidOperationException("Candidate cleanup cannot apply online metadata.");
                plan = UnifiedCandidateCleanupPlanner.Build(
                    initial,
                    request.ExpectedGeneration,
                    request.ExpectedRevision,
                    request.GroupSelections,
                    issues);
                metadataTemplates = [];
            }
            else if (metadataMutation)
            {
                if (request.GroupSelections.Count != 0 || request.MetadataReview is null)
                    throw new InvalidOperationException("Metadata mutation requires only a current metadata review workspace.");
                plan = new([], [], [], 0);
                metadataTemplates = BuildMetadataOperations(
                    initial, request, issues, out omittedMetadataAuthorFields);
            }
            else
            {
                if (request.GroupSelections.Count != 0 || request.MetadataReview is not null)
                    throw new InvalidOperationException("Author normalization accepts no Candidate or metadata review selections.");
                plan = new([], [], [], 0);
                metadataTemplates = BuildAuthorNormalizationOperations(initial);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            issues.Add(Block("CANDIDATE.PLAN_INVALID", exception.Message));
            return Result(UnifiedCandidateCleanupState.PreflightFailed, 0, 0, 0, 0);
        }

        IEditionCoverStagingSession? stagedCovers = null;
        EditionCoverStagingRequest[] coverRequests = metadataTemplates
            .Where(value => value.Cover is not null)
            .Select(value => new EditionCoverStagingRequest(value.StagingKey, value.Cover!))
            .ToArray();
        if (coverRequests.Length > 0)
        {
            progress?.Report(new("Preparing approved metadata covers.", 0, coverRequests.Length));
            Progress<EditionCoverStagingProgress> coverProgress = new(value => progress?.Report(new(
                $"Preparing approved metadata covers: {value.CacheHits:N0} cache hit(s), "
                + $"{value.Downloads:N0} downloaded, {value.Omitted:N0} omitted.",
                value.Completed,
                value.Total)));
            EditionCoverStagingResult staging = await coverStager.StageAsync(
                coverRequests, coverProgress, cancellationToken).ConfigureAwait(false);
            if (!staging.IsSuccess)
            {
                issues.Add(Block("CANDIDATE.METADATA_COVER_STAGING_FAILED",
                    $"Approved metadata covers could not be staged ({staging.FailureCode ?? "METADATA_COVER.STAGING_FAILED"}; "
                    + $"completed {staging.CompletedCount:N0} of {staging.TotalCount:N0})."));
                return Result(UnifiedCandidateCleanupState.PreflightFailed, 0, 0, 0, plan.SkippedGroupCount);
            }
            stagedCovers = staging.Session;
            IReadOnlyDictionary<string, string> skippedCovers = staging.SkippedCovers
                ?? new Dictionary<string, string>();
            if (skippedCovers.Count > 0)
            {
                omittedMetadataCovers = skippedCovers.Count;
                metadataTemplates = metadataTemplates.Where(value =>
                    value.Cover is null || !skippedCovers.ContainsKey(value.StagingKey)).ToArray();
                issues.Add(new(
                    "CANDIDATE.METADATA_COVERS_OMITTED",
                    ExecutionIssueSeverity.Warning,
                    $"Omitted {omittedMetadataCovers:N0} approved cover(s) after bounded transient download failures; other metadata remains eligible."));
                foreach ((string reason, int count) in skippedCovers.Values
                             .GroupBy(value => value, StringComparer.Ordinal)
                             .Select(group => (group.Key, group.Count()))
                             .OrderBy(value => value.Key, StringComparer.Ordinal))
                    LogMetadataCoversOmitted(_logger, executionId.ToString(), reason, count);
            }
        }
        await using IEditionCoverStagingSession? coverSession = stagedCovers;

        int totalOperations = checked(plan.TotalOperations + metadataTemplates.Length);
        if (totalOperations == 0)
        {
            LibraryStateSessionOutcome completed = await AdvancePhaseAsync(
                request.LibraryRoot,
                candidateCleanup ? LibraryWorkflowPhase.CandidateCleanupCompleted : LibraryWorkflowPhase.Completed)
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

        LogCleanupStarted(_logger, executionId.ToString(), totalOperations);
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
                discovery.Tool!, request.LibraryRoot, initial.Snapshot.Identity.CalibreLibraryUuid,
                coverSession?.StagingRoot),
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
            WorkerPlannedOperation[] operations = BuildWorkerOperations(plan, metadataTemplates, coverSession);
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
                    string workerFailure = WorkerFailureCode(workerResult);
                    int failedOperationIndex = workerResult.OperationResults
                        .Select((value, index) => (value, index))
                        .First(value => !value.value.IsSuccess).index;
                    string failedOperationKind = chunk[failedOperationIndex].Operation.Metadata?.Field.ToString()
                        ?? chunk[failedOperationIndex].Operation.Kind.ToString();
                    int uncommittedSuccessfulPrefix = workerResult.OperationResults
                        .Take(failedOperationIndex).Count(value => value.IsSuccess);
                    await MarkUncertainAsync(
                        "CANDIDATE.WORKER_CHUNK_FAILED",
                        $"The persistent Calibre worker did not complete a Candidate cleanup chunk unambiguously ({workerFailure}).")
                        .ConfigureAwait(false);
                    LogWorkerChunkFailure(
                        _logger, executionId.ToString(), chunkNumber, completedOperations,
                        operations.Length, failedOperationIndex + 1, failedOperationKind,
                        uncommittedSuccessfulPrefix, workerFailure);
                    issues.Add(Block(
                        "CANDIDATE.WORKER_CHUNK_FAILED",
                        $"The persistent Calibre worker could not complete Candidate cleanup ({workerFailure}; "
                        + $"chunk operation {failedOperationIndex + 1:N0} was {failedOperationKind}; "
                        + $"completed {completedOperations:N0} of {operations.Length:N0} committed operations)."));
                    return Result(UnifiedCandidateCleanupState.PartiallyCompleted,
                        transferred, removedFormats, removedRecords, plan.SkippedGroupCount);
                }

                LibraryState current = Current();
                LibraryStateRevision revision = current.Revision;
                DateTimeOffset appliedAt = AppliedAt(current);
                List<LibraryStateDelta> deltas = new(chunk.Length);
                for (int index = 0; index < chunk.Length; index++)
                {
                    deltas.Add(CreateDelta(
                        current.GenerationId,
                        revision,
                        appliedAt,
                        chunk[index],
                        workerResult.OperationResults[index]));
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
                updatedMetadataFields += chunk.Zip(workerResult.OperationResults).Count(pair =>
                    pair.First.Operation.Kind == CalibreMutationOperationKind.SetMetadata
                    && !pair.Second.IsSkipped);
                skippedMetadataFields += workerResult.OperationResults.Count(value => value.IsSkipped);
                foreach (CalibreMutationOperationResult skipped in workerResult.OperationResults.Where(
                             value => value.IsSkipped))
                {
                    string reason = skipped.SkipCode ?? "metadata_unchanged";
                    metadataSkipReasons[reason] = metadataSkipReasons.GetValueOrDefault(reason) + 1;
                }
                completedOperations += chunk.Length;
            }

            if (skippedMetadataFields > 0)
            {
                issues.Add(new(
                    "CANDIDATE.METADATA_SKIPPED_UNCHANGED",
                    ExecutionIssueSeverity.Warning,
                    $"Skipped {skippedMetadataFields:N0} metadata field(s) after verifying their complete pre-write state was unchanged."));
                foreach ((string reason, int count) in metadataSkipReasons.OrderBy(value => value.Key, StringComparer.Ordinal))
                    LogMetadataSkippedUnchanged(_logger, executionId.ToString(), reason, count);
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
            LibraryStateSessionOutcome completed = await AdvancePhaseAsync(
                request.LibraryRoot,
                candidateCleanup ? LibraryWorkflowPhase.CandidateCleanupCompleted : LibraryWorkflowPhase.Completed)
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
            progress?.Report(new(
                authorNormalization ? "Author normalization completed." : "Candidate cleanup completed.",
                operations.Length,
                operations.Length));
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

        async Task<LibraryStateSessionOutcome> AdvancePhaseAsync(
            string root,
            LibraryWorkflowPhase phase)
        {
            LibraryState state = libraryState.GetCurrent(root)
                ?? throw new InvalidOperationException("Authoritative Candidate state disappeared.");
            DateTimeOffset publishedAt = clock.GetUtcNow().ToUniversalTime();
            if (publishedAt < state.ProjectedAtUtc) publishedAt = state.ProjectedAtUtc;
            return await libraryState.AdvanceWorkflowAsync(
                root, phase, publishedAt, CancellationToken.None)
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
            issues.ToArray(),
            updatedMetadataFields,
            skippedMetadataFields,
            omittedMetadataCovers,
            omittedMetadataAuthorFields);
    }

    private static MetadataOperationTemplate[] BuildMetadataOperations(
        LibraryState state,
        ExecuteUnifiedCandidateCleanupRequest request,
        List<ExecutionIssue> issues,
        out int omittedAuthorFields)
    {
        omittedAuthorFields = 0;
        MetadataReviewWorkspace? workspace = request.MetadataReview;
        if (workspace is null) return [];
        if (workspace.GenerationId != request.ExpectedGeneration
            || workspace.Revision != request.ExpectedRevision
            || !PathsEqual(workspace.LibraryRoot, request.LibraryRoot))
            throw new InvalidOperationException("The metadata review workspace is stale.");

        Dictionary<UnifiedCandidateGroupId, UnifiedCandidateCleanupSelection> selections =
            request.GroupSelections.ToDictionary(value => value.GroupId);
        Dictionary<UnifiedCandidateGroupId, UnifiedCandidateGroup> groups =
            state.Snapshot.UnifiedCandidateGroups.ToDictionary(value => value.Id);
        HashSet<CalibreBookId> targets = [];
        List<MetadataOperationTemplate> operations = [];
        foreach (ReviewedMetadataSubject reviewed in workspace.Subjects.Where(value => value.Apply))
        {
            MetadataReviewSubject subject = reviewed.Subject;
            EditionMetadataCandidate candidate = subject.Proposal.Candidate
                ?? throw new InvalidOperationException("Checked metadata has no edition candidate.");
            EditionMetadataProviderIdentity provider = subject.Proposal.PrimaryProvider
                ?? throw new InvalidOperationException("Checked metadata has no primary provider.");
            MetadataReviewDecisionKey key = MetadataReviewDecisionPolicy.CreateKey(
                subject, workspace.GenerationId, workspace.Revision);
            if (reviewed.IsOverride && reviewed.DecisionKey != key)
                throw new InvalidOperationException("A metadata review override is stale.");
            if (subject.UnifiedGroupId is { } groupId)
            {
                if (!groups.TryGetValue(groupId, out UnifiedCandidateGroup? group)
                    || !selections.TryGetValue(groupId, out UnifiedCandidateCleanupSelection? selection))
                    throw new InvalidOperationException("A metadata review group is stale.");
                if (selection.Skip) continue;
                if (selection.KeeperBookId != subject.TargetBookId
                    || !group.Members.ToHashSet().SetEquals(subject.Members))
                    throw new InvalidOperationException("A metadata review keeper is stale.");
            }
            else if (subject.Members.Count != 1)
            {
                throw new InvalidOperationException("A singleton metadata review subject is stale.");
            }
            if (!state.Snapshot.Books.Any(value => value.Id == subject.TargetBookId)
                || !targets.Add(subject.TargetBookId))
                throw new InvalidOperationException("A metadata target is missing or duplicated.");
            CalibreMetadataSourceIdentity source = new(
                provider.Id, provider.Version, candidate.EditionId, subject.Proposal.PolicyVersion);
            Add(LibraryMetadataField.Title, [candidate.Title]);
            if (EditionMetadataAuthorWritePolicy.TryPrepare(candidate.Authors, out IReadOnlyList<string> authors))
                Add(LibraryMetadataField.Authors, authors);
            else
                omittedAuthorFields++;
            if (candidate.Identifiers.Count > 0)
                Add(LibraryMetadataField.Identifiers,
                    candidate.Identifiers
                        .GroupBy(value => value.Type, StringComparer.Ordinal)
                        .Select(group => group
                            .OrderByDescending(value => value.Type == "isbn" && value.Value.Length == 13)
                            .ThenBy(value => value.Value, StringComparer.Ordinal)
                            .First())
                        .OrderBy(value => value.Type, StringComparer.Ordinal)
                        .Select(value => $"{value.Type}:{value.Value}")
                        .ToArray());
            if (candidate.Publisher is not null) Add(LibraryMetadataField.Publisher, [candidate.Publisher]);
            if (candidate.PublicationDate is not null)
            {
                EditionPublicationDate date = candidate.PublicationDate;
                DateTimeOffset value = new(
                    date.Year, date.Month ?? 1, date.Day ?? 1, 0, 0, 0, TimeSpan.Zero);
                Add(LibraryMetadataField.PublicationDate, [value.ToString("O", CultureInfo.InvariantCulture)]);
            }
            string[] writableLanguages = candidate.Languages.Where(IsWritableLanguage).ToArray();
            if (writableLanguages.Length > 0)
                Add(LibraryMetadataField.Languages, writableLanguages);
            if (candidate.Series is not null) Add(LibraryMetadataField.Series, [candidate.Series]);
            if (candidate.SeriesIndex is not null)
                Add(LibraryMetadataField.SeriesIndex,
                    [candidate.SeriesIndex.Value.ToString(CultureInfo.InvariantCulture)]);
            if (candidate.Cover is not null)
                operations.Add(new(
                    subject.Id.Value,
                    subject.TargetBookId,
                    LibraryMetadataField.Cover,
                    [],
                    source,
                    candidate.Cover));

            void Add(LibraryMetadataField field, IEnumerable<string> values) => operations.Add(new(
                subject.Id.Value, subject.TargetBookId, field, values.ToArray(), source, null));
        }
        if (omittedAuthorFields > 0)
        {
            issues.Add(new(
            "CANDIDATE.METADATA_AUTHORS_OMITTED",
            ExecutionIssueSeverity.Warning,
            $"Omitted {omittedAuthorFields:N0} ambiguous combined provider author field(s); other approved metadata remains eligible."));
        }
        return operations.ToArray();
    }

    private static MetadataOperationTemplate[] BuildAuthorNormalizationOperations(LibraryState state)
    {
        CalibreMetadataSourceIdentity source = new(
            "calibre-author-normalization",
            "calibre-author-normalization/1.2.0",
            "local-library",
            "author-name-normalization/1.2.0");
        return state.Snapshot.Books.OrderBy(value => value.Id.Value).Select(book =>
        {
            if (!PipeAuthorNameNormalizationPolicy.TryNormalizeBook(
                    book.Authors, out IReadOnlyList<string> displays, out IReadOnlyList<string> sorts))
                return null;
            return new MetadataOperationTemplate(
                $"author-normalization/{book.Id.Value}",
                book.Id,
                LibraryMetadataField.Authors,
                displays,
                source,
                null,
                sorts);
        }).Where(value => value is not null).Select(value => value!).ToArray();
    }

    private static WorkerPlannedOperation[] BuildWorkerOperations(
        UnifiedCandidateCleanupPlan plan,
        IReadOnlyList<MetadataOperationTemplate> metadata,
        IEditionCoverStagingSession? covers) =>
    [
        .. metadata.Select(value => new WorkerPlannedOperation(
            CalibreMutationOperation.SetMetadata(
                $"candidate-metadata:{value.RecordId.Value}:{value.Field.ToString().ToLowerInvariant()}",
                value.RecordId,
                new(
                    value.Field,
                    value.Values,
                    value.Source,
                    value.Cover is null ? null : covers?.Covers[value.StagingKey].FileName,
                    value.Cover is null ? null : covers?.Covers[value.StagingKey].Fingerprint,
                    value.AuthorSortValues)),
            null)),
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
        WorkerPlannedOperation planned,
        CalibreMutationOperationResult result) => result.IsSkipped
        ? new SkipMetadataLibraryStateDelta(
            generation, revision, planned.Operation.OperationId, appliedAt,
            planned.Operation.RecordId, planned.Operation.Metadata!.Field,
            result.SkipCode ?? "metadata_unchanged")
        : planned.Operation.Kind switch
        {
            CalibreMutationOperationKind.SetMetadata => new SetMetadataLibraryStateDelta(
                generation,
                revision,
                planned.Operation.OperationId,
                appliedAt,
                planned.Operation.RecordId,
                planned.Operation.Metadata!.Field,
                result.VerifiedMetadataValues!,
                result.VerifiedManagedPath,
                result.VerifiedAuthorSort,
                result.VerifiedAuthorSortValues),
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

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsWritableLanguage(string value)
    {
        string normalized = CandidateMetadataNormalizer.NormalizeLanguage(value) ?? string.Empty;
        return WritableLanguages.Contains(normalized);
    }

    private static string WorkerFailureCode(CalibreMutationChunkResult result) =>
        result.FailureCode
        ?? result.OperationResults.FirstOrDefault(value => !value.IsSuccess)?.FailureCode
        ?? "CALIBRE_WORKER_CHUNK_FAILED";

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

    [LoggerMessage(83, LogLevel.Error,
        "Unified Candidate cleanup {ExecutionId} worker chunk {ChunkNumber} failed after {CompletedOperations} of {TotalOperations} committed operations. ChunkOperation={ChunkOperation}, OperationKind={OperationKind}, UncommittedSuccessfulPrefix={UncommittedSuccessfulPrefix}, FailureCode={FailureCode}.")]
    private static partial void LogWorkerChunkFailure(
        ILogger logger,
        string executionId,
        int chunkNumber,
        int completedOperations,
        int totalOperations,
        int chunkOperation,
        string operationKind,
        int uncommittedSuccessfulPrefix,
        string failureCode);

    [LoggerMessage(84, LogLevel.Warning,
        "Unified Candidate cleanup {ExecutionId} skipped {Count} verified-unchanged metadata operation(s). SkipCode={SkipCode}.")]
    private static partial void LogMetadataSkippedUnchanged(
        ILogger logger,
        string executionId,
        string skipCode,
        int count);

    [LoggerMessage(85, LogLevel.Warning,
        "Unified Candidate cleanup {ExecutionId} omitted {Count} transiently unavailable metadata cover(s). FailureCode={FailureCode}.")]
    private static partial void LogMetadataCoversOmitted(
        ILogger logger,
        string executionId,
        string failureCode,
        int count);

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

    private sealed record MetadataOperationTemplate(
        string StagingKey,
        CalibreBookId RecordId,
        LibraryMetadataField Field,
        IReadOnlyList<string> Values,
        CalibreMetadataSourceIdentity Source,
        EditionCoverReference? Cover,
        IReadOnlyList<string>? AuthorSortValues = null);
}
