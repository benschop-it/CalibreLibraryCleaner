using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed class ExecuteBulkExactDuplicateCleanupUseCase(
    ILibraryStateSession libraryState,
    ICalibreToolDiscovery toolDiscovery,
    ICalibreCommandGateway commandGateway,
    IExactDuplicateFormatStaging staging,
    ILibraryMutationLease mutationLease,
    ICleanupExecutionIdGenerator executionIds,
    IClock clock)
{
    public async Task<BulkExactDuplicateCleanupResult> ExecuteAsync(
        ExecuteBulkExactDuplicateCleanupRequest request,
        IProgress<BulkExactDuplicateCleanupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CleanupExecutionId executionId = executionIds.Create();
        List<ExecutionIssue> issues = [];
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
        Dictionary<FormatKey, StagedExactDuplicateFormat> staged = [];
        int completed = 0;
        int removedFormats = 0;
        int mergedRecords = 0;
        int removedRecords = 0;
        bool mutationStarted = false;
        try
        {
            progress?.Report(new("Preparing complementary formats for record merging.", 0, plan.TotalOperations));
            foreach (TransferOperation transfer in plan.Transfers.Where(value => value.NeedsAdd))
            {
                cancellationToken.ThrowIfCancellationRequested();
                StagedExactDuplicateFormat value = await staging.StageAsync(
                    executionId, request.LibraryRoot, transfer.SourceRecordId,
                    transfer.SourceFormat, cancellationToken).ConfigureAwait(false);
                staged.Add(new(transfer.SourceRecordId, transfer.SourceFormat.Format), value);
            }

            foreach (TransferOperation transfer in plan.Transfers.Where(value => value.NeedsAdd))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lease.IsHeld) throw new InvalidOperationException("The cleanup lease was lost.");
                StagedExactDuplicateFormat value = staged[new(transfer.SourceRecordId, transfer.SourceFormat.Format)];
                progress?.Report(new(
                    $"Moving {transfer.SourceFormat.Format} from record {transfer.SourceRecordId.Value} to {transfer.TargetRecordId.Value}.",
                    completed, plan.TotalOperations));
                mutationStarted = true;
                CalibreCommandResult command = await commandGateway.AddOrReplaceFormatAsync(new(
                    discovery.Tool!, request.LibraryRoot, transfer.TargetRecordId,
                    transfer.SourceFormat.Format, value.PhysicalPath, value.Fingerprint),
                    CancellationToken.None).ConfigureAwait(false);
                if (!command.IsSuccess)
                    return await FailUncertainAsync("BULK_EXACT.ADD_FORMAT_FAILED",
                        "Calibre could not move a complementary format.", transfer.SourceRecordId,
                        transfer.SourceFormat.Format).ConfigureAwait(false);
                LibraryState current = Current();
                LibraryStateSessionOutcome applied = await libraryState.ApplyAsync(request.LibraryRoot,
                    new AddOrReplaceFormatLibraryStateDelta(current.GenerationId, current.Revision,
                        $"bulk-add:{transfer.TargetRecordId.Value}:{transfer.SourceFormat.Format}", AppliedAt(current),
                        transfer.TargetRecordId, transfer.SourceFormat.Format,
                        transfer.SourceFormat.Fingerprint!, null), CancellationToken.None).ConfigureAwait(false);
                if (!applied.IsSuccess)
                    return FailProjection("The moved format could not be committed to projected state.");
                completed++;
            }

            foreach (FormatRemovalOperation removal in plan.FormatRemovals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lease.IsHeld) throw new InvalidOperationException("The cleanup lease was lost.");
                progress?.Report(new(
                    $"Removing duplicate {removal.Format.Format} from record {removal.RecordId.Value}.",
                    completed, plan.TotalOperations));
                mutationStarted = true;
                CalibreCommandResult command = await commandGateway.RemoveFormatAsync(new(
                    discovery.Tool!, request.LibraryRoot, removal.RecordId, removal.Format.Format),
                    CancellationToken.None).ConfigureAwait(false);
                if (!command.IsSuccess)
                    return await FailUncertainAsync("BULK_EXACT.REMOVE_FORMAT_FAILED",
                        "Calibre could not remove a duplicate format.", removal.RecordId,
                        removal.Format.Format).ConfigureAwait(false);
                LibraryState current = Current();
                LibraryStateSessionOutcome applied = await libraryState.ApplyAsync(request.LibraryRoot,
                    new RemoveFormatLibraryStateDelta(current.GenerationId, current.Revision,
                        $"bulk-remove-format:{removal.RecordId.Value}:{removal.Format.Format}", AppliedAt(current),
                        removal.RecordId, removal.Format.Format, removal.Format.Fingerprint!),
                    CancellationToken.None).ConfigureAwait(false);
                if (!applied.IsSuccess)
                    return FailProjection("The removed format could not be committed to projected state.");
                removedFormats++;
                completed++;
            }

            foreach (CalibreBookId recordId in plan.RecordsToRemove)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lease.IsHeld) throw new InvalidOperationException("The cleanup lease was lost.");
                CalibreBook record = Current().Snapshot.Books.Single(value => value.Id == recordId);
                if (record.Formats.Count != 0)
                {
                    issues.Add(new("BULK_EXACT.RECORD_NOT_EMPTY", ExecutionIssueSeverity.Warning,
                        "A record retained formats and was not removed.", recordId));
                    continue;
                }
                progress?.Report(new($"Removing empty record {recordId.Value}.", completed, plan.TotalOperations));
                mutationStarted = true;
                CalibreCommandResult command = await commandGateway.RemoveRecordAsync(new(
                    discovery.Tool!, request.LibraryRoot, recordId), CancellationToken.None).ConfigureAwait(false);
                if (!command.IsSuccess)
                    return await FailUncertainAsync("BULK_EXACT.REMOVE_RECORD_FAILED",
                        "Calibre could not remove an empty record.", recordId, null).ConfigureAwait(false);
                LibraryState current = Current();
                LibraryStateSessionOutcome applied = await libraryState.ApplyAsync(request.LibraryRoot,
                    new RemoveRecordLibraryStateDelta(current.GenerationId, current.Revision,
                        $"bulk-remove-record:{recordId.Value}", AppliedAt(current), recordId),
                    CancellationToken.None).ConfigureAwait(false);
                if (!applied.IsSuccess)
                    return FailProjection("The removed record could not be committed to projected state.");
                if (plan.MergedRecordIds.Contains(recordId)) mergedRecords++;
                removedRecords++;
                completed++;
            }

            progress?.Report(new("Duplicate cleanup completed.", plan.TotalOperations, plan.TotalOperations));
            return Result(BulkExactDuplicateCleanupState.Completed, removedFormats,
                mergedRecords, removedRecords, plan.SkippedRecordCount);
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
            issues.Add(Block("BULK_EXACT.EXECUTION_FAILED", exception.Message));
            return Result(mutationStarted ? BulkExactDuplicateCleanupState.PartiallyCompleted
                : BulkExactDuplicateCleanupState.PreflightFailed, removedFormats, mergedRecords,
                removedRecords, plan.SkippedRecordCount);
        }
        finally
        {
            try { await staging.CleanupAsync(executionId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
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
            issues.Add(Block("BULK_EXACT.DELTA_COMMIT_FAILED", explanation));
            return Result(BulkExactDuplicateCleanupState.PartiallyCompleted,
                removedFormats, mergedRecords, removedRecords, plan.SkippedRecordCount);
        }

        async Task<BulkExactDuplicateCleanupResult> FailUncertainAsync(
            string code,
            string explanation,
            CalibreBookId? recordId,
            string? format)
        {
            await MarkUncertainAsync(code, explanation, recordId).ConfigureAwait(false);
            issues.Add(Block(code, explanation, recordId, format));
            return Result(BulkExactDuplicateCleanupState.PartiallyCompleted,
                removedFormats, mergedRecords, removedRecords, plan.SkippedRecordCount);
        }

        Task<LibraryStateSessionOutcome> MarkUncertainAsync(
            string code,
            string explanation,
            CalibreBookId? recordId) => libraryState.MarkUncertainAsync(request.LibraryRoot,
            new(code, explanation, clock.GetUtcNow(), recordId?.Value.ToString(
                System.Globalization.CultureInfo.InvariantCulture)), CancellationToken.None);
    }

    private static BulkPlan BuildPlan(
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

    private readonly record struct FormatKey(CalibreBookId RecordId, string Format);
    private sealed record FormatRemovalOperation(CalibreBookId RecordId, BookFormat Format);
    private sealed record TransferOperation(
        CalibreBookId SourceRecordId,
        CalibreBookId TargetRecordId,
        BookFormat SourceFormat,
        bool NeedsAdd);
    private sealed record BulkPlan(
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
