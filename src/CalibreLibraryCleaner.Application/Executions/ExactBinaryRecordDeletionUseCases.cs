using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed class PrepareExactBinaryRecordDeletionUseCase(
    IExecutionLibraryScanner scanLibrary,
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
        LibraryScanOutcome scan = await scanLibrary.ScanFreshAsync(
            request.LibraryRoot, progress, cancellationToken).ConfigureAwait(false);
        if (!scan.IsSuccess)
            issues.Add(Block("BINARY_EXECUTION.SCAN_FAILED", "A fresh read-only library scan could not be completed."));
        else
            issues.AddRange(ToExecutionIssues(ValidateExactBinaryCleanupPlanUseCase.EvaluateStaleness(
                request.Plan, scan.Snapshot!, cancellationToken)));

        BackupDestinationValidation destination = await backupStore.ValidateDestinationAsync(
            request.LibraryRoot, request.BackupDestination, EstimateBackupBytes(request.Plan),
            cancellationToken).ConfigureAwait(false);
        issues.AddRange(destination.Issues);
        return new(request.Plan, tool.Tool, scan.Snapshot?.Identity.LibraryRoot,
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
        if (plan.Definition.RecordIdsToRemove.Count == 0
            || plan.Definition.RecordIdsToRemove.Contains(plan.Definition.RetainedFormat.RecordId))
            issues.Add(Block("BINARY_EXECUTION.RECORD_SELECTION_INVALID", "The deletion set is empty or contains the keeper record."));
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
    IExecutionLibraryScanner scanLibrary,
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
        int removedCount = 0;
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
            LibraryScanOutcome initialScan = await scanLibrary.ScanFreshAsync(
                request.LibraryRoot, null, cancellationToken).ConfigureAwait(false);
            if (!tool.IsSuccess || !initialScan.IsSuccess)
            {
                if (!initialScan.IsSuccess) issues.Add(Block("BINARY_EXECUTION.SCAN_FAILED", "The fresh preflight scan failed."));
                return Result(ExactBinaryRecordDeletionState.PreflightFailed);
            }
            issues.AddRange(PrepareExactBinaryRecordDeletionUseCase.ToExecutionIssues(
                ValidateExactBinaryCleanupPlanUseCase.EvaluateStaleness(request.Plan, initialScan.Snapshot!, cancellationToken)));
            if (HasBlockingIssues()) return Result(ExactBinaryRecordDeletionState.PreflightFailed);

            CalibreBookId[] involved = request.Plan.Definition.ExpectedRecords.Select(value => value.RecordId).ToArray();
            Sha256Digest unaffectedBaseline = ExecutionSnapshotDigestPolicy.ComputeUnaffected(initialScan.Snapshot!, involved);
            workspace = await workspaceStore.CreateWorkspaceAsync(executionId,
                destination.CanonicalDestinationIdentity!, cancellationToken).ConfigureAwait(false);
            await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "ExecutionCreated",
                "Exact-binary marked-record execution created."), cancellationToken).ConfigureAwait(false);

            progress?.Report(new("Creating complete external backups of the keeper and marked records.", 0,
                request.Plan.Definition.RecordIdsToRemove.Count, false));
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

            LibraryScanOutcome finalGate = await scanLibrary.ScanFreshAsync(
                request.LibraryRoot, null, cancellationToken).ConfigureAwait(false);
            if (!finalGate.IsSuccess)
                issues.Add(Block("BINARY_EXECUTION.FINAL_GATE_SCAN_FAILED", "The library could not be scanned after backup."));
            else
            {
                issues.AddRange(PrepareExactBinaryRecordDeletionUseCase.ToExecutionIssues(
                    ValidateExactBinaryCleanupPlanUseCase.EvaluateStaleness(request.Plan, finalGate.Snapshot!, cancellationToken)));
                if (ExecutionSnapshotDigestPolicy.ComputeUnaffected(finalGate.Snapshot!, involved) != unaffectedBaseline)
                    issues.Add(Block("BINARY_EXECUTION.UNRELATED_STATE_CHANGED", "Unrelated library state changed during backup."));
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
            foreach (CalibreBookId recordId in request.Plan.Definition.RecordIdsToRemove)
            {
                if (cancellationToken.IsCancellationRequested)
                    return Result(mutationStarted
                        ? ExactBinaryRecordDeletionState.PartiallyApplied
                        : ExactBinaryRecordDeletionState.CancelledBeforeMutation);

                (CalibreToolDescriptor? currentTool, LibrarySnapshot? gateSnapshot) = await ValidateCommandGateAsync(
                    request, workspace, manifest, expectedTool, involved, unaffectedBaseline, removed,
                    lease, issues, cancellationToken).ConfigureAwait(false);
                if (currentTool is null || gateSnapshot is null || HasBlockingIssues())
                    return Result(mutationStarted
                        ? ExactBinaryRecordDeletionState.PartiallyApplied
                        : ExactBinaryRecordDeletionState.PreflightFailed);

                mutationStarted = true;
                await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "RecordRemovalStarting",
                    "Typed non-permanent Calibre record removal starting.", recordId, "remove"),
                    CancellationToken.None).ConfigureAwait(false);
                progress?.Report(new($"Removing marked duplicate record {recordId.Value} through Calibre.",
                    removedCount, request.Plan.Definition.RecordIdsToRemove.Count, true));
                CalibreCommandResult command = await commandGateway.RemoveRecordAsync(new(
                    currentTool, request.LibraryRoot, recordId), CancellationToken.None).ConfigureAwait(false);
                await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "RecordRemovalCommand",
                    command.IsSuccess ? "Calibre record removal command completed." : "Calibre record removal command failed.",
                    recordId, command.CommandKind, command.ExitCode, command.FailureCode),
                    CancellationToken.None).ConfigureAwait(false);
                if (!command.IsSuccess)
                {
                    issues.Add(Block("BINARY_EXECUTION.REMOVE_FAILED",
                        "Calibre failed to remove the marked duplicate record.", recordId));
                    return Result(ExactBinaryRecordDeletionState.PartiallyApplied);
                }

                removed.Add(recordId);
                LibraryScanOutcome verification = await scanLibrary.ScanFreshAsync(
                    request.LibraryRoot, null, CancellationToken.None).ConfigureAwait(false);
                if (!verification.IsSuccess || !VerifyCurrentState(request.Plan, verification.Snapshot,
                        removed, unaffectedBaseline, issues))
                {
                    if (!verification.IsSuccess)
                        issues.Add(Block("BINARY_EXECUTION.VERIFICATION_SCAN_FAILED",
                            "The library could not be scanned after record removal.", recordId));
                    return Result(ExactBinaryRecordDeletionState.VerificationFailed);
                }
                removedCount++;
                await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "RecordRemovalVerified",
                    "Marked record absence and all preservation expectations verified.", recordId),
                    CancellationToken.None).ConfigureAwait(false);
            }

            await backupStore.AppendAuditAsync(workspace, new(clock.GetUtcNow(), "Completed",
                "All marked duplicate records were removed and the final state verified."),
                CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new("Marked duplicate-record cleanup completed.", removedCount,
                request.Plan.Definition.RecordIdsToRemove.Count, true));
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
            executionId, state, issues.ToArray(), workspace?.BundlePath, removedCount, mutationStarted);
        bool HasBlockingIssues() => issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError);
    }

    private async Task<(CalibreToolDescriptor? Tool, LibrarySnapshot? Snapshot)> ValidateCommandGateAsync(
        ExecuteExactBinaryRecordDeletionRequest request,
        ExecutionWorkspace workspace,
        ExactBinaryRecordBackupManifest manifest,
        CalibreToolDescriptor expectedTool,
        IReadOnlyCollection<CalibreBookId> involved,
        Sha256Digest unaffectedBaseline,
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
        LibraryScanOutcome scan = await scanLibrary.ScanFreshAsync(
            request.LibraryRoot, null, cancellationToken).ConfigureAwait(false);
        if (!scan.IsSuccess)
            issues.Add(Block("BINARY_EXECUTION.COMMAND_GATE_SCAN_FAILED", "A fresh scan failed immediately before deletion."));
        else
            VerifyCurrentState(request.Plan, scan.Snapshot, removed, unaffectedBaseline, issues);
        return (issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError) ? null : tool.Tool,
            scan.Snapshot);
    }

    private static bool VerifyCurrentState(
        ExactBinaryCleanupPlan plan,
        LibrarySnapshot? snapshot,
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
                     || !ExactBinaryExpectedState.Matches(expected, book))
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
