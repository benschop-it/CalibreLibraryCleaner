using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed class PrepareExactBinaryRecordDeletionUseCase(
    ILibraryStateSession libraryState,
    ICalibreToolDiscovery toolDiscovery,
    IExecutionBackupStore backupStore)
{
    public async Task<ExactBinaryRecordDeletionPreparation> ExecuteAsync(
        PrepareExactBinaryRecordDeletionRequest request,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<ExecutionIssue> issues = ValidateApprovedPlan(request.Plan).ToList();
        if (issues.Count > 0) return new(request.Plan, null, null, null, issues);

        CalibreToolDiscoveryResult tool = await toolDiscovery.DiscoverAndProbeAsync(
            request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        issues.AddRange(tool.Issues);
        LibraryState? state = libraryState.GetCurrent(request.LibraryRoot);
        if (state is null)
            issues.Add(Block("BINARY_EXECUTION.STATE_NOT_LOADED", "Run an explicit library scan before preparing exact duplicate cleanup."));
        else if (!state.IsAuthoritative)
            issues.Add(Block("BINARY_EXECUTION.STATE_UNCERTAIN", "The projected library state is uncertain; run an explicit scan before cleanup."));
        else
            issues.AddRange(ToExecutionIssues(ValidateExactBinaryCleanupPlanUseCase.EvaluateStaleness(
                request.Plan, state.Snapshot, cancellationToken)));

        BackupDestinationValidation destination = await backupStore.ValidateDestinationAsync(
            request.LibraryRoot, request.BackupDestination, EstimateBackupBytes(request.Plan),
            cancellationToken).ConfigureAwait(false);
        issues.AddRange(destination.Issues);
        return new(request.Plan, tool.Tool, state?.Snapshot.Identity.LibraryRoot,
            destination.CanonicalDestinationIdentity, issues);
    }

    internal static IReadOnlyList<ExecutionIssue> ValidateApprovedPlan(ExactBinaryCleanupPlan plan)
    {
        List<ExecutionIssue> issues = [];
        if (plan.State != CleanupPlanState.Approved || plan.Approval?.ContentDigest != plan.ContentDigest)
            issues.Add(Block("BINARY_EXECUTION.APPROVAL_REQUIRED", "The marked-record plan is not explicitly approved."));
        if (!plan.Validation.IsValid
            || ExactBinaryCleanupPlanContentDigestPolicy.Compute(plan.Definition) != plan.ContentDigest
            || plan.InputIdentity.DefinitionDigest != plan.ContentDigest)
            issues.Add(Block("BINARY_EXECUTION.PLAN_INVALID", "The marked-record plan is blocked or its immutable body changed."));
        if (plan.Definition.FormatRemovals.Count == 0
            || plan.Definition.FormatRemovals.Any(value => value.RecordId == plan.Definition.RetainedFormat.RecordId
                && value.Format == plan.Definition.RetainedFormat.Format))
            issues.Add(Block("BINARY_EXECUTION.FORMAT_SELECTION_INVALID", "The format-removal set is empty or contains the retained format."));
        return issues;
    }

    internal static long EstimateBackupBytes(ExactBinaryCleanupPlan plan)
    {
        long raw = plan.Definition.ExpectedRecords
            .SelectMany(value => value.Formats)
            .Aggregate(0L, (total, value) => checked(total + value.Fingerprint.SizeInBytes));
        return checked(raw * 3 + 64L * 1024 * 1024);
    }

    internal static IEnumerable<ExecutionIssue> ToExecutionIssues(IEnumerable<CleanupPlanIssue> issues) =>
        issues.Select(value => new ExecutionIssue(
            $"BINARY_EXECUTION.{value.Code}",
            ExecutionIssueSeverity.BlockingError,
            value.Explanation,
            value.RecordId,
            value.Format));

    internal static ExecutionIssue Block(
        string code,
        string explanation,
        CalibreBookId? recordId = null,
        string? format = null) => new(code, ExecutionIssueSeverity.BlockingError, explanation, recordId, format);
}

public sealed class ExecuteExactBinaryRecordDeletionUseCase(
    ILibraryStateSession libraryState,
    ICalibreToolDiscovery toolDiscovery,
    ICalibreCommandGateway commandGateway,
    ILibraryMutationLease executionLease,
    IExecutionBackupStore workspaceStore,
    IExactBinaryRecordBackupStore backupStore,
    ICleanupExecutionIdGenerator executionIds,
    IExactBinaryRecordDeletionConfirmation confirmation,
    IClock clock)
{
    public async Task<ExactBinaryRecordDeletionResult> ExecuteAsync(
        ExecuteExactBinaryRecordDeletionRequest request,
        IProgress<ExactBinaryRecordDeletionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CleanupExecutionId executionId = executionIds.Create();
        List<ExecutionIssue> issues = PrepareExactBinaryRecordDeletionUseCase.ValidateApprovedPlan(request.Plan).ToList();
        ExecutionWorkspace? workspace = null;
        ExactBinaryRecordBackupManifest? manifest = null;
        bool mutationStarted = false;
        int removedFormatCount = 0;
        int removedCount = 0;
        HashSet<(CalibreBookId RecordId, string Format, string RelativePath)> removedFormats = [];
        HashSet<CalibreBookId> removed = [];
        if (!request.OtherCalibreMutatorsClosed || !request.FullLibraryCopyAcknowledged)
            issues.Add(Block("BINARY_EXECUTION.ACKNOWLEDGEMENT_REQUIRED",
                "Confirm that Calibre is closed and that a full library copy or equivalent recovery backup exists."));
        if (issues.Count > 0) return Result(ExactBinaryRecordDeletionState.PreflightFailed);

        LibraryMutationLeaseAcquisition acquisition = await executionLease.TryAcquireAsync(new(
            executionId.ToString(), LibraryMutationKind.Cleanup, request.LibraryRoot,
            request.Plan.InputIdentity.LibraryUuid, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        issues.AddRange(acquisition.Issues);
        if (!acquisition.IsAcquired) return Result(ExactBinaryRecordDeletionState.PreflightFailed);

        await using ILibraryMutationLeaseHandle lease = acquisition.Lease!;
        try
        {
            BackupDestinationValidation destination = await workspaceStore.ValidateDestinationAsync(
                request.LibraryRoot, request.BackupDestination,
                PrepareExactBinaryRecordDeletionUseCase.EstimateBackupBytes(request.Plan),
                cancellationToken).ConfigureAwait(false);
            issues.AddRange(destination.Issues);
            if (!destination.IsValid) return Result(ExactBinaryRecordDeletionState.PreflightFailed);

            CalibreToolDiscoveryResult tool = await toolDiscovery.DiscoverAndProbeAsync(
                request.LibraryRoot, cancellationToken).ConfigureAwait(false);
            issues.AddRange(tool.Issues);
            LibraryState? initialState = libraryState.GetCurrent(request.LibraryRoot);
            if (!tool.IsSuccess || initialState is null || !initialState.IsAuthoritative)
            {
                if (initialState is null)
                    issues.Add(Block("BINARY_EXECUTION.STATE_NOT_LOADED", "Run an explicit library scan before exact duplicate cleanup."));
                else if (!initialState.IsAuthoritative)
                    issues.Add(Block("BINARY_EXECUTION.STATE_UNCERTAIN", "The projected library state is uncertain; run an explicit scan before cleanup."));
                return Result(ExactBinaryRecordDeletionState.PreflightFailed);
            }
            issues.AddRange(PrepareExactBinaryRecordDeletionUseCase.ToExecutionIssues(
                ValidateExactBinaryCleanupPlanUseCase.EvaluateStaleness(request.Plan, initialState.Snapshot, cancellationToken)));
            if (HasBlockingIssues()) return Result(ExactBinaryRecordDeletionState.PreflightFailed);

            CalibreBookId[] involved = request.Plan.Definition.ExpectedRecords.Select(value => value.RecordId).ToArray();
            Sha256Digest unaffectedBaseline = ExecutionSnapshotDigestPolicy.ComputeUnaffected(initialState.Snapshot, involved);
            workspace = await workspaceStore.CreateWorkspaceAsync(executionId,
                destination.CanonicalDestinationIdentity!, cancellationToken).ConfigureAwait(false);
            await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "ExecutionCreated",
                "Exact-binary marked-record execution created."), cancellationToken).ConfigureAwait(false);

            int totalOperations = request.Plan.Definition.FormatRemovals.Count
                + request.Plan.Definition.RecordIdsToRemove.Count;
            progress?.Report(new("Creating complete external backups of all affected records and formats.", 0,
                totalOperations, false));
            ExactBinaryRecordBackupInputs inputs = await backupStore.CreateInputsAsync(
                workspace, request.Plan, request.LibraryRoot, tool.Tool!, request.ApplicationVersion,
                cancellationToken).ConfigureAwait(false);
            issues.AddRange(inputs.Issues);
            if (!inputs.IsSuccess) return Result(ExactBinaryRecordDeletionState.BackupFailed);

            foreach (ExpectedRecordState record in request.Plan.Definition.ExpectedRecords)
            {
                CalibreCommandResult export = await commandGateway.ExportRecordAsync(new(
                    tool.Tool!, request.LibraryRoot, record.RecordId, inputs.ExportDirectories[record.RecordId]),
                    cancellationToken).ConfigureAwait(false);
                await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "BackupExport",
                    export.IsSuccess ? "Calibre record export completed." : "Calibre record export failed.",
                    record.RecordId, export.CommandKind, export.ExitCode, export.FailureCode),
                    cancellationToken).ConfigureAwait(false);
                if (!export.IsSuccess)
                {
                    issues.Add(Block("BINARY_EXECUTION.BACKUP_EXPORT_FAILED",
                        "Calibre could not export a complete involved record.", record.RecordId));
                    return Result(ExactBinaryRecordDeletionState.BackupFailed);
                }
            }

            ExactBinaryRecordBackupResult backup = await backupStore.VerifyAndSealAsync(
                inputs, request.Plan, cancellationToken).ConfigureAwait(false);
            issues.AddRange(backup.Issues);
            if (!backup.IsSuccess) return Result(ExactBinaryRecordDeletionState.BackupFailed);
            manifest = backup.Manifest!;
            await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "BackupVerified",
                $"Backup manifest {manifest.ManifestDigest.Value} verified."), cancellationToken).ConfigureAwait(false);

            LibraryState? finalGate = libraryState.GetCurrent(request.LibraryRoot);
            if (finalGate is null || !finalGate.IsAuthoritative)
                issues.Add(Block("BINARY_EXECUTION.STATE_UNAVAILABLE", "Authoritative projected state became unavailable during backup."));
            else
            {
                issues.AddRange(PrepareExactBinaryRecordDeletionUseCase.ToExecutionIssues(
                    ValidateExactBinaryCleanupPlanUseCase.EvaluateStaleness(request.Plan, finalGate.Snapshot, cancellationToken)));
                if (ExecutionSnapshotDigestPolicy.ComputeUnaffected(finalGate.Snapshot, involved) != unaffectedBaseline)
                    issues.Add(Block("BINARY_EXECUTION.UNRELATED_STATE_CHANGED", "Unrelated projected state changed during backup."));
            }
            issues.AddRange(await backupStore.VerifyAvailableAsync(workspace, manifest, cancellationToken).ConfigureAwait(false));
            if (!lease.IsHeld) issues.Add(Block("BINARY_EXECUTION.LEASE_LOST", "The cleanup lease was lost before deletion."));
            if (HasBlockingIssues()) return Result(ExactBinaryRecordDeletionState.PreflightFailed);

            if (!await confirmation.ConfirmAsync(request.Plan, manifest, cancellationToken).ConfigureAwait(false))
            {
                await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "DeletionDeclined",
                    "Final marked-record deletion confirmation was declined."), cancellationToken).ConfigureAwait(false);
                return Result(ExactBinaryRecordDeletionState.CancelledBeforeMutation);
            }

            CalibreToolDescriptor expectedTool = tool.Tool!;
            foreach (ExpectedFormatState format in request.Plan.Definition.FormatRemovals)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Result(mutationStarted
                        ? ExactBinaryRecordDeletionState.PartiallyApplied
                        : ExactBinaryRecordDeletionState.CancelledBeforeMutation);

                (CalibreToolDescriptor? currentTool, LibrarySnapshot? gateSnapshot) = await ValidateCommandGateAsync(
                    request, workspace, manifest, expectedTool, involved, unaffectedBaseline, removedFormats, removed,
                    lease, issues, cancellationToken).ConfigureAwait(false);
                if (currentTool is null || gateSnapshot is null || HasBlockingIssues())
                    return Result(mutationStarted
                        ? ExactBinaryRecordDeletionState.PartiallyApplied
                        : ExactBinaryRecordDeletionState.PreflightFailed);

                mutationStarted = true;
                await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "FormatRemovalStarting",
                    "Typed Calibre duplicate-format removal starting.", format.RecordId, "remove_format"),
                    CancellationToken.None).ConfigureAwait(false);
                progress?.Report(new($"Removing duplicate {format.Format} from record {format.RecordId.Value} through Calibre.",
                    removedFormatCount + removedCount, totalOperations, true));
                CalibreCommandResult command = await commandGateway.RemoveFormatAsync(new(
                    currentTool, request.LibraryRoot, format.RecordId, format.Format), CancellationToken.None).ConfigureAwait(false);
                await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "FormatRemovalCommand",
                    command.IsSuccess ? "Calibre format removal command completed." : "Calibre format removal command failed.",
                    format.RecordId, command.CommandKind, command.ExitCode, command.FailureCode),
                    CancellationToken.None).ConfigureAwait(false);
                if (!command.IsSuccess)
                {
                    await MarkUncertainAsync(request.LibraryRoot, "REMOVE_FORMAT_COMMAND_FAILED",
                        "Calibre did not return an unambiguous successful format-removal result.",
                        $"remove-format:{format.RecordId.Value}:{format.Format}").ConfigureAwait(false);
                    issues.Add(Block("BINARY_EXECUTION.REMOVE_FORMAT_FAILED",
                        "Calibre failed to remove a planned duplicate format.", format.RecordId, format.Format));
                    return Result(ExactBinaryRecordDeletionState.PartiallyApplied);
                }

                LibraryState currentState = libraryState.GetCurrent(request.LibraryRoot)!;
                string operationId = $"remove-format:{format.RecordId.Value}:{format.Format}";
                LibraryStateSessionOutcome projection = await libraryState.ApplyAsync(request.LibraryRoot,
                    new RemoveFormatLibraryStateDelta(currentState.GenerationId, currentState.Revision,
                        operationId, clock.GetUtcNow(), format.RecordId, format.Format, format.Fingerprint),
                    CancellationToken.None).ConfigureAwait(false);
                if (!projection.IsSuccess)
                {
                    issues.Add(Block("BINARY_EXECUTION.DELTA_REJECTED",
                        "The successful format removal could not be applied to projected state.", format.RecordId, format.Format));
                    return Result(ExactBinaryRecordDeletionState.VerificationFailed);
                }
                removedFormats.Add((format.RecordId, format.Format, format.RelativePath));
                if (!VerifyCurrentState(request.Plan, projection.State!.Snapshot,
                        removedFormats, removed, unaffectedBaseline, issues))
                    return Result(ExactBinaryRecordDeletionState.VerificationFailed);
                removedFormatCount++;
                await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "FormatRemovalVerified",
                    "Duplicate format absence and all preservation expectations verified.", format.RecordId),
                    CancellationToken.None).ConfigureAwait(false);
            }

            foreach (CalibreBookId recordId in request.Plan.Definition.RecordIdsToRemove)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Result(mutationStarted
                        ? ExactBinaryRecordDeletionState.PartiallyApplied
                        : ExactBinaryRecordDeletionState.CancelledBeforeMutation);

                (CalibreToolDescriptor? currentTool, LibrarySnapshot? gateSnapshot) = await ValidateCommandGateAsync(
                    request, workspace, manifest, expectedTool, involved, unaffectedBaseline, removedFormats, removed,
                    lease, issues, cancellationToken).ConfigureAwait(false);
                if (currentTool is null || gateSnapshot is null || HasBlockingIssues())
                    return Result(mutationStarted
                        ? ExactBinaryRecordDeletionState.PartiallyApplied
                        : ExactBinaryRecordDeletionState.PreflightFailed);

                mutationStarted = true;
                await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "RecordRemovalStarting",
                    "Typed non-permanent Calibre record removal starting.", recordId, "remove"),
                    CancellationToken.None).ConfigureAwait(false);
                progress?.Report(new($"Removing empty duplicate record {recordId.Value} through Calibre.",
                    removedFormatCount + removedCount, totalOperations, true));
                CalibreCommandResult command = await commandGateway.RemoveRecordAsync(new(
                    currentTool, request.LibraryRoot, recordId), CancellationToken.None).ConfigureAwait(false);
                await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "RecordRemovalCommand",
                    command.IsSuccess ? "Calibre record removal command completed." : "Calibre record removal command failed.",
                    recordId, command.CommandKind, command.ExitCode, command.FailureCode),
                    CancellationToken.None).ConfigureAwait(false);
                if (!command.IsSuccess)
                {
                    await MarkUncertainAsync(request.LibraryRoot, "REMOVE_RECORD_COMMAND_FAILED",
                        "Calibre did not return an unambiguous successful record-removal result.",
                        $"remove-record:{recordId.Value}").ConfigureAwait(false);
                    issues.Add(Block("BINARY_EXECUTION.REMOVE_FAILED",
                        "Calibre failed to remove a record that was verified empty after format cleanup.", recordId));
                    return Result(ExactBinaryRecordDeletionState.PartiallyApplied);
                }

                LibraryState currentState = libraryState.GetCurrent(request.LibraryRoot)!;
                string operationId = $"remove-record:{recordId.Value}";
                LibraryStateSessionOutcome projection = await libraryState.ApplyAsync(request.LibraryRoot,
                    new RemoveRecordLibraryStateDelta(currentState.GenerationId, currentState.Revision,
                        operationId, clock.GetUtcNow(), recordId), CancellationToken.None).ConfigureAwait(false);
                if (!projection.IsSuccess)
                {
                    issues.Add(Block("BINARY_EXECUTION.DELTA_REJECTED",
                        "The successful record removal could not be applied to projected state.", recordId));
                    return Result(ExactBinaryRecordDeletionState.VerificationFailed);
                }
                removed.Add(recordId);
                if (!VerifyCurrentState(request.Plan, projection.State!.Snapshot,
                        removedFormats, removed, unaffectedBaseline, issues))
                    return Result(ExactBinaryRecordDeletionState.VerificationFailed);
                removedCount++;
                await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "RecordRemovalVerified",
                    "Marked record absence and all preservation expectations verified.", recordId),
                    CancellationToken.None).ConfigureAwait(false);
            }

            await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "Completed",
                "All planned duplicate formats and derived empty records were removed and verified."),
                CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new("Exact duplicate format cleanup completed.", totalOperations,
                totalOperations, true));
            return Result(ExactBinaryRecordDeletionState.Completed);
        }
        catch (OperationCanceledException)
        {
            return Result(mutationStarted
                ? ExactBinaryRecordDeletionState.PartiallyApplied
                : ExactBinaryRecordDeletionState.CancelledBeforeMutation);
        }
        catch (Exception)
        {
            issues.Add(Block("BINARY_EXECUTION.UNEXPECTED_FAILURE",
                mutationStarted
                    ? "An unexpected failure occurred after deletion began. Restore from the verified bundle or full library copy."
                    : "An unexpected failure occurred before deletion began."));
            return Result(mutationStarted
                ? ExactBinaryRecordDeletionState.PartiallyApplied
                : ExactBinaryRecordDeletionState.PreflightFailed);
        }

        ExactBinaryRecordDeletionResult Result(ExactBinaryRecordDeletionState state) => new(
            executionId, state, issues.ToArray(), workspace?.BundlePath, removedFormatCount, removedCount, mutationStarted);
        bool HasBlockingIssues() => issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError);
        Task<LibraryStateSessionOutcome> MarkUncertainAsync(
            string root,
            string code,
            string explanation,
            string operationId) => libraryState.MarkUncertainAsync(
                root, new(code, explanation, clock.GetUtcNow(), operationId), CancellationToken.None);
    }

    private async Task<(CalibreToolDescriptor? Tool, LibrarySnapshot? Snapshot)> ValidateCommandGateAsync(
        ExecuteExactBinaryRecordDeletionRequest request,
        ExecutionWorkspace workspace,
        ExactBinaryRecordBackupManifest manifest,
        CalibreToolDescriptor expectedTool,
        IReadOnlyCollection<CalibreBookId> involved,
        Sha256Digest unaffectedBaseline,
        IReadOnlySet<(CalibreBookId RecordId, string Format, string RelativePath)> removedFormats,
        IReadOnlySet<CalibreBookId> removed,
        ILibraryMutationLeaseHandle lease,
        List<ExecutionIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!lease.IsHeld) issues.Add(Block("BINARY_EXECUTION.LEASE_LOST", "The cleanup lease was lost before a record removal."));
        if (ExactBinaryCleanupPlanContentDigestPolicy.Compute(request.Plan.Definition) != request.Plan.ContentDigest
            || request.Plan.Approval?.ContentDigest != request.Plan.ContentDigest)
            issues.Add(Block("BINARY_EXECUTION.PLAN_CHANGED", "The approved marked-record plan changed before deletion."));
        issues.AddRange(await backupStore.VerifyAvailableAsync(workspace, manifest, cancellationToken).ConfigureAwait(false));
        CalibreToolDiscoveryResult tool = await toolDiscovery.DiscoverAndProbeAsync(
            request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        issues.AddRange(tool.Issues);
        if (!tool.IsSuccess || tool.Tool!.Identity != expectedTool.Identity)
            issues.Add(Block("BINARY_EXECUTION.TOOL_CHANGED", "The trusted Calibre tool changed before deletion."));
        LibraryState? state = libraryState.GetCurrent(request.LibraryRoot);
        if (state is null || !state.IsAuthoritative)
            issues.Add(Block("BINARY_EXECUTION.STATE_UNAVAILABLE", "Authoritative projected state is unavailable before mutation."));
        else
            VerifyCurrentState(request.Plan, state.Snapshot, removedFormats, removed, unaffectedBaseline, issues);
        return (issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError) ? null : tool.Tool,
            state?.Snapshot);
    }

    private static bool VerifyCurrentState(
        ExactBinaryCleanupPlan plan,
        LibrarySnapshot? snapshot,
        IReadOnlySet<(CalibreBookId RecordId, string Format, string RelativePath)> removedFormats,
        IReadOnlySet<CalibreBookId> removed,
        Sha256Digest unaffectedBaseline,
        List<ExecutionIssue> issues)
    {
        if (snapshot is null) return false;
        Dictionary<CalibreBookId, CalibreBook> current = snapshot.Books.ToDictionary(value => value.Id);
        foreach (ExpectedRecordState expected in plan.Definition.ExpectedRecords)
        {
            if (removed.Contains(expected.RecordId))
            {
                if (current.ContainsKey(expected.RecordId))
                    issues.Add(Block("BINARY_EXECUTION.RECORD_STILL_PRESENT",
                        "A removed record remains present after Calibre reported success.", expected.RecordId));
            }
            else if (!current.TryGetValue(expected.RecordId, out CalibreBook? book)
                     || !ExactBinaryExpectedState.Matches(expected, book, removedFormats))
            {
                issues.Add(Block("BINARY_EXECUTION.PRESERVATION_MISMATCH",
                    "A keeper or pending marked record changed unexpectedly.", expected.RecordId));
            }
        }
        CalibreBookId[] involved = plan.Definition.ExpectedRecords.Select(value => value.RecordId).ToArray();
        if (ExecutionSnapshotDigestPolicy.ComputeUnaffected(snapshot, involved) != unaffectedBaseline)
            issues.Add(Block("BINARY_EXECUTION.UNRELATED_STATE_CHANGED", "Unrelated library state changed during deletion."));
        return !issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError);
    }

    private static ExecutionIssue Block(
        string code,
        string explanation,
        CalibreBookId? recordId = null,
        string? format = null) => PrepareExactBinaryRecordDeletionUseCase.Block(code, explanation, recordId, format);
}
