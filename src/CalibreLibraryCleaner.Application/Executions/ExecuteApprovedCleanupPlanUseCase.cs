using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed class ExecuteApprovedCleanupPlanUseCase(
    IExecutionLibraryScanner scanLibrary,
    ICalibreToolDiscovery toolDiscovery,
    ICalibreCommandGateway commandGateway,
    ICleanupExecutionLease executionLease,
    IExecutionBackupStore backupStore,
    IExecutionJournalStore journalStore,
    IExecutionHistoryStore historyStore,
    ICleanupExecutionIdGenerator executionIds,
    IDestructiveExecutionConfirmation destructiveConfirmation,
    IClock clock) : IExecuteApprovedCleanupPlan
{
    public async Task<CleanupExecutionResult> ExecuteAsync(
        ExecuteCleanupPlanRequest request,
        IProgress<CleanupExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CleanupExecutionId executionId = executionIds.Create();
        List<ExecutionIssue> issues = [];
        CleanupExecutionCapabilityResult capability = CleanupExecutionCapabilityPolicy.Evaluate(request.Plan);
        issues.AddRange(capability.Issues);
        if (!capability.IsSupported)
            return Result(executionId, CleanupExecutionState.PreflightFailed, CleanupExecutionDisposition.Failed,
                CleanupExecutionFailureClassification.Preflight, issues, null, null, null, false);

        CleanupExecution execution;
        try
        {
            execution = CleanupExecution.Create(executionId, request.Plan, request.Confirmation, capability.Graph!);
        }
        catch (ArgumentException)
        {
            issues.Add(Block("EXECUTION.CONFIRMATION_INVALID", "The local execution confirmation is not bound to this exact plan."));
            return Result(executionId, CleanupExecutionState.PreflightFailed, CleanupExecutionDisposition.Failed,
                CleanupExecutionFailureClassification.Preflight, issues, null, null, null, false);
        }

        execution = execution.Transition(CleanupExecutionState.AcquiringLease);
        progress?.Report(new(CleanupExecutionProgressPhase.AcquiringLease, "Acquiring the exclusive application execution lease.", 0,
            execution.Graph.Operations.Count, false));
        ExecutionLeaseAcquisition acquisition = await executionLease.TryAcquireAsync(new(
            executionId, request.LibraryRoot, request.Plan.InputIdentity.LibraryUuid, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        issues.AddRange(acquisition.Issues);
        if (!acquisition.IsAcquired)
            return Result(executionId, CleanupExecutionState.PreflightFailed, CleanupExecutionDisposition.Failed,
                CleanupExecutionFailureClassification.Preflight, issues, null, null, null, false);

        await using ICleanupExecutionLeaseHandle lease = acquisition.Lease!;
        IExecutionJournalSession? journal = null;
        ExecutionWorkspace? workspace = null;
        VerifiedBackupManifest? manifest = null;
        string? journalIdentity = null;
        Sha256Digest unaffectedBaseline = default;
        bool terminalPersisted = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long requiredBytes = ExecutionPreflightPolicy.EstimateRequiredBackupBytes(request.Plan);
            BackupDestinationValidation destination = await backupStore.ValidateDestinationAsync(
                request.LibraryRoot, request.BackupDestination, requiredBytes, cancellationToken).ConfigureAwait(false);
            issues.AddRange(destination.Issues);
            if (!destination.IsValid)
            {
                execution = execution.Transition(CleanupExecutionState.PreflightFailed);
                return await FinishAsync(execution, issues, workspace, journal, manifest,
                    CleanupExecutionFailureClassification.Preflight, terminalPersisted).ConfigureAwait(false);
            }

            if (!string.Equals(request.Confirmation.BackupDestinationIdentity,
                    destination.CanonicalDestinationIdentity, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Block("EXECUTION.CONFIRMATION_DESTINATION_CHANGED", "The backup destination changed after local confirmation."));
                execution = execution.Transition(CleanupExecutionState.PreflightFailed);
                return await FinishAsync(execution, issues, workspace, journal, manifest,
                    CleanupExecutionFailureClassification.Preflight, terminalPersisted).ConfigureAwait(false);
            }

            JournalReconciliationResult reconciliation = await journalStore.ReconcileAsync(
                destination.CanonicalDestinationIdentity!, request.Plan.InputIdentity.LibraryUuid, cancellationToken).ConfigureAwait(false);
            issues.AddRange(reconciliation.Issues);
            bool priorRecovery = await historyStore.HasRecoveryRequiredAsync(
                request.Plan.InputIdentity.LibraryUuid, request.LibraryRoot, cancellationToken).ConfigureAwait(false);
            if (reconciliation.RecoveryRequired || priorRecovery)
            {
                issues.Add(Block("EXECUTION.PRIOR_RECOVERY_REQUIRED", "A prior incomplete execution requires recovery before another plan may run."));
                execution = execution.Transition(CleanupExecutionState.PreflightFailed);
                return await FinishAsync(execution, issues, workspace, journal, manifest,
                    CleanupExecutionFailureClassification.Preflight, terminalPersisted).ConfigureAwait(false);
            }

            workspace = await backupStore.CreateWorkspaceAsync(
                executionId, destination.CanonicalDestinationIdentity!, cancellationToken).ConfigureAwait(false);
            journal = await journalStore.CreateAsync(new(workspace, request.Plan, request.LibraryRoot,
                request.ApplicationVersion, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            journalIdentity = journal.JournalIdentity;
            await AppendAsync(journal, execution, "ExecutionCreated", "Execution workspace and durable journal created.", cancellationToken).ConfigureAwait(false);

            execution = execution.Transition(CleanupExecutionState.PreflightValidating);
            progress?.Report(new(CleanupExecutionProgressPhase.Preflight, "Running a fresh read-only preflight scan.", 0,
                execution.Graph.Operations.Count, false));
            await AppendAsync(journal, execution, "PreflightStarted", "Fresh read-only preflight started.", cancellationToken).ConfigureAwait(false);

            CalibreToolDiscoveryResult discovery = await toolDiscovery.DiscoverAndProbeAsync(
                request.LibraryRoot, cancellationToken).ConfigureAwait(false);
            issues.AddRange(discovery.Issues);
            LibraryScanOutcome firstScan = await scanLibrary.ScanFreshAsync(request.LibraryRoot, null, cancellationToken).ConfigureAwait(false);
            if (!discovery.IsSuccess || !firstScan.IsSuccess)
            {
                if (!firstScan.IsSuccess) issues.Add(Block("EXECUTION.SCAN_FAILED", "The fresh read-only preflight scan failed."));
                execution = execution.Transition(CleanupExecutionState.PreflightFailed);
                await AppendFailureAsync(journal, execution, "PreflightFailed", issues, cancellationToken).ConfigureAwait(false);
                return await FinishAsync(execution, issues, workspace, journal, manifest,
                    CleanupExecutionFailureClassification.Preflight, terminalPersisted).ConfigureAwait(false);
            }

            if (!request.Confirmation.Matches(request.Plan, discovery.Tool!.Identity,
                    destination.CanonicalDestinationIdentity!, firstScan.Snapshot!.Identity.LibraryRoot,
                    capability.Graph!.Digest))
                issues.Add(Block("EXECUTION.CONFIRMATION_CHANGED", "The plan, library, tool, or destination no longer matches the local execution confirmation."));
            issues.AddRange(ExecutionPreflightPolicy.Evaluate(request.Plan, firstScan.Snapshot!));
            if (issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError))
            {
                execution = execution.Transition(CleanupExecutionState.PreflightFailed);
                await AppendFailureAsync(journal, execution, "PreflightFailed", issues, cancellationToken).ConfigureAwait(false);
                return await FinishAsync(execution, issues, workspace, journal, manifest,
                    CleanupExecutionFailureClassification.Preflight, terminalPersisted).ConfigureAwait(false);
            }

            unaffectedBaseline = ExecutionSnapshotDigestPolicy.ComputeUnaffected(
                firstScan.Snapshot!, request.Plan.Definition.InvolvedRecordIds);
            execution = execution.Transition(CleanupExecutionState.ReadyForBackup);
            await AppendAsync(journal, execution, "PreflightVerified", "All live preconditions and capability checks passed.",
                cancellationToken, issues).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            execution = execution.Transition(CleanupExecutionState.BackingUp);
            progress?.Report(new(CleanupExecutionProgressPhase.Backup, "Creating complete external backups.", 0,
                execution.Graph.Operations.Count, false));
            await AppendAsync(journal, execution, "BackupStarted", "Complete external backup creation started.", cancellationToken).ConfigureAwait(false);
            ExecutionBackupInputs inputs = await backupStore.CreateInputsAsync(new(
                workspace, request.Plan, request.Confirmation, request.LibraryRoot, discovery.Tool, request.ApplicationVersion,
                unaffectedBaseline, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            issues.AddRange(inputs.Issues);
            if (!inputs.IsSuccess)
            {
                execution = execution.Transition(CleanupExecutionState.BackupFailed);
                await AppendFailureAsync(journal, execution, "BackupFailed", issues, cancellationToken).ConfigureAwait(false);
                return await FinishAsync(execution, issues, workspace, journal, manifest,
                    CleanupExecutionFailureClassification.Backup, terminalPersisted).ConfigureAwait(false);
            }

            foreach (CalibreBookId recordId in request.Plan.Definition.InvolvedRecordIds.OrderBy(value => value.Value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CalibreCommandResult export = await commandGateway.ExportRecordAsync(new(
                    discovery.Tool, request.LibraryRoot, recordId, inputs.ExportDirectories[recordId]), cancellationToken).ConfigureAwait(false);
                await AppendCommandAsync(journal, execution, null, export, false, cancellationToken).ConfigureAwait(false);
                if (!export.IsSuccess)
                {
                    issues.Add(Block("EXECUTION.BACKUP_EXPORT_FAILED", "Calibre could not export a complete affected record.", recordId));
                    execution = execution.Transition(CleanupExecutionState.BackupFailed);
                    await AppendFailureAsync(journal, execution, "BackupFailed", issues, cancellationToken).ConfigureAwait(false);
                    return await FinishAsync(execution, issues, workspace, journal, manifest,
                        CleanupExecutionFailureClassification.Backup, terminalPersisted).ConfigureAwait(false);
                }
            }

            ExecutionBackupResult backup = await backupStore.VerifyAndSealAsync(
                new(inputs, request.Plan, request.LibraryRoot, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            issues.AddRange(backup.Issues);
            if (!backup.IsSuccess)
            {
                execution = execution.Transition(CleanupExecutionState.BackupFailed);
                await AppendFailureAsync(journal, execution, "BackupFailed", issues, cancellationToken).ConfigureAwait(false);
                return await FinishAsync(execution, issues, workspace, journal, manifest,
                    CleanupExecutionFailureClassification.Backup, terminalPersisted).ConfigureAwait(false);
            }

            manifest = backup.Manifest!;
            execution = execution.AttachVerifiedBackup(manifest);
            await AppendAsync(journal, execution, "BackupVerified", "The complete backup manifest was independently hash-verified.", cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(CleanupExecutionProgressPhase.FinalMutationGate, "Repeating every precondition after backup.", 0,
                execution.Graph.Operations.Count, false));
            CalibreToolDiscoveryResult secondDiscovery = await toolDiscovery.DiscoverAndProbeAsync(
                request.LibraryRoot, cancellationToken).ConfigureAwait(false);
            LibraryScanOutcome secondScan = await scanLibrary.ScanFreshAsync(request.LibraryRoot, null, cancellationToken).ConfigureAwait(false);
            issues.AddRange(secondDiscovery.Issues);
            if (!secondDiscovery.IsSuccess || secondDiscovery.Tool!.Identity != discovery.Tool.Identity
                || !secondScan.IsSuccess
                || CleanupPlanContentDigestPolicy.Compute(request.Plan.Definition) != request.Plan.ContentDigest
                || !lease.IsHeld)
            {
                issues.Add(Block("EXECUTION.FINAL_GATE_CHANGED", "The tool, plan, or library could not be revalidated after backup."));
            }
            else
            {
                if (!request.Confirmation.Matches(request.Plan, secondDiscovery.Tool.Identity,
                        workspace.CanonicalBackupDestinationIdentity, secondScan.Snapshot!.Identity.LibraryRoot,
                        capability.Graph!.Digest))
                    issues.Add(Block("EXECUTION.CONFIRMATION_CHANGED",
                        "The local confirmation no longer matches the final mutation gate."));
                issues.AddRange(ExecutionPreflightPolicy.Evaluate(request.Plan, secondScan.Snapshot!));
                if (ExecutionSnapshotDigestPolicy.ComputeUnaffected(secondScan.Snapshot!, request.Plan.Definition.InvolvedRecordIds) != unaffectedBaseline)
                    issues.Add(Block("EXECUTION.UNRELATED_STATE_CHANGED", "Unrelated library state changed during backup."));
                issues.AddRange(await backupStore.VerifyAvailableAsync(workspace, manifest, cancellationToken).ConfigureAwait(false));
            }

            if (issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError))
            {
                execution = execution.Transition(CleanupExecutionState.ExecutionFailedBeforeMutation);
                await AppendFailureAsync(journal, execution, "FinalMutationGateFailed", issues, cancellationToken).ConfigureAwait(false);
                return await FinishAsync(execution, issues, workspace, journal, manifest,
                    CleanupExecutionFailureClassification.CommandBeforeMutation, terminalPersisted).ConfigureAwait(false);
            }

            execution = execution.Transition(CleanupExecutionState.ReadyToExecute);
            HashSet<string> processedRetentions = [];
            HashSet<CalibreBookId> removedRecords = [];
            LibrarySnapshot currentSnapshot = secondScan.Snapshot!;
            foreach (CleanupExecutionOperation operation in execution.Graph.Operations.Where(value => value.Phase == ExecutionOperationPhase.Precondition))
            {
                execution = execution.SatisfyNoOp(operation.Id);
                if (operation.Kind == ExecutionOperationKind.VerifyTargetFormatPreserved && operation.Format is not null)
                    processedRetentions.Add(request.Plan.Definition.FormatRetentions.Single(value => value.Format == operation.Format).Id);
                await AppendOperationVerifiedAsync(journal, execution, operation, true, cancellationToken).ConfigureAwait(false);
            }

            int completedOperations = execution.Operations.Count(value => value.Status == ExecutionOperationStatus.SatisfiedNoOp);
            foreach (CleanupExecutionOperation operation in execution.Graph.ConstructiveOperations)
            {
                FormatRetentionInstruction retention = request.Plan.Definition.FormatRetentions.Single(value => value.Format == operation.Format);
                if (TargetAlreadyMatches(currentSnapshot, request.Plan.Definition.TargetRecordId, retention))
                {
                    execution = execution.SatisfyNoOp(operation.Id);
                    processedRetentions.Add(retention.Id);
                    completedOperations++;
                    await AppendOperationVerifiedAsync(journal, execution, operation, true, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                    return await CancelBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                        completedOperations, progress).ConfigureAwait(false);
                (bool gatePassed, CalibreToolDescriptor? currentTool, LibrarySnapshot? gateSnapshot,
                    IReadOnlyList<ExecutionIssue> gateIssues) = await VerifyPerCommandGateAsync(
                    request, discovery.Tool, workspace, manifest, lease, processedRetentions,
                    removedRecords, unaffectedBaseline, cancellationToken).ConfigureAwait(false);
                issues.AddRange(gateIssues);
                if (!gatePassed)
                    return await FailBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                        CleanupExecutionFailureClassification.ConstructiveCommand).ConfigureAwait(false);
                currentSnapshot = gateSnapshot!;

                if (!execution.MutationStarted)
                {
                    if (!await PersistMutationGuardAsync(execution, workspace, journal, manifest, issues).ConfigureAwait(false))
                        return await FailBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                            CleanupExecutionFailureClassification.Journal).ConfigureAwait(false);
                    await AppendMutationStartingAsync(journal, execution, CancellationToken.None).ConfigureAwait(false);
                    execution = execution.MarkMutationStarting();
                }

                execution = execution.StartOperation(operation.Id);
                progress?.Report(new(CleanupExecutionProgressPhase.Constructive,
                    $"Applying retained {operation.Format} format through Calibre.", completedOperations,
                    execution.Graph.Operations.Count, true, operation.Id));
                await AppendOperationStartingAsync(journal, execution, operation, "add_format", CancellationToken.None).ConfigureAwait(false);
                BackupFormatKey key = new(retention.SourceState.RecordId, retention.Format, retention.SourceState.RelativePath);
                CalibreCommandResult command = await commandGateway.AddOrReplaceFormatAsync(new(
                    currentTool!, request.LibraryRoot, request.Plan.Definition.TargetRecordId,
                    retention.Format, backup.RawFormatBackupPaths[key],
                    retention.SourceState.Fingerprint), CancellationToken.None).ConfigureAwait(false);
                await AppendCommandAsync(journal, execution, operation.Id, command, true, CancellationToken.None).ConfigureAwait(false);
                if (!command.IsSuccess)
                {
                    execution = execution.MarkOperationFailed(operation.Id, command.FailureCode ?? "EXECUTION.CALIBRE_COMMAND_FAILED",
                        CleanupExecutionFailureClassification.ConstructiveCommand).RequireRecovery(CleanupExecutionFailureClassification.ConstructiveCommand);
                    issues.Add(Block("EXECUTION.CONSTRUCTIVE_COMMAND_FAILED", "A constructive Calibre operation failed; no destructive operation was started.", operation.SourceRecordId, operation.Format));
                    await CaptureFailureScanAsync(request, journal, execution, unaffectedBaseline, processedRetentions, removedRecords).ConfigureAwait(false);
                    return await FinishAsync(execution, issues, workspace, journal, manifest,
                        CleanupExecutionFailureClassification.ConstructiveCommand, terminalPersisted).ConfigureAwait(false);
                }

                execution = execution.MarkOperationSucceeded(operation.Id);
                processedRetentions.Add(retention.Id);
                LibraryScanOutcome verificationScan = await scanLibrary.ScanFreshAsync(request.LibraryRoot, null, CancellationToken.None).ConfigureAwait(false);
                if (!verificationScan.IsSuccess)
                {
                    execution = execution.MarkOperationFailed(operation.Id, "EXECUTION.VERIFICATION_SCAN_FAILED",
                        CleanupExecutionFailureClassification.IntermediateVerification).RequireRecovery(CleanupExecutionFailureClassification.IntermediateVerification);
                    issues.Add(Block("EXECUTION.VERIFICATION_SCAN_FAILED", "The library could not be scanned after a constructive operation."));
                    return await FinishAsync(execution, issues, workspace, journal, manifest,
                        CleanupExecutionFailureClassification.IntermediateVerification, terminalPersisted).ConfigureAwait(false);
                }

                ExecutionVerificationResult verification = CleanupExecutionVerificationPolicy.VerifyConstructiveState(
                    request.Plan, verificationScan.Snapshot!, processedRetentions, removedRecords,
                    unaffectedBaseline, clock.GetUtcNow());
                issues.AddRange(verification.Issues);
                await AppendVerificationAsync(journal, execution, operation.Id, verification, CancellationToken.None).ConfigureAwait(false);
                if (!verification.IsVerified)
                {
                    execution = execution.MarkOperationFailed(operation.Id, "EXECUTION.INTERMEDIATE_VERIFICATION_FAILED",
                        CleanupExecutionFailureClassification.IntermediateVerification).RequireRecovery(CleanupExecutionFailureClassification.IntermediateVerification);
                    return await FinishAsync(execution, issues, workspace, journal, manifest,
                        CleanupExecutionFailureClassification.IntermediateVerification, terminalPersisted).ConfigureAwait(false);
                }

                execution = execution.MarkOperationVerified(operation.Id);
                currentSnapshot = verificationScan.Snapshot!;
                completedOperations++;
                await AppendOperationVerifiedAsync(journal, execution, operation, false, CancellationToken.None).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    return await CancelBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                        completedOperations, progress).ConfigureAwait(false);
            }

            progress?.Report(new(CleanupExecutionProgressPhase.IntermediateVerification,
                "Verifying the complete constructive state before any removal.", completedOperations,
                execution.Graph.Operations.Count, execution.MutationStarted));
            ExecutionVerificationResult intermediate = CleanupExecutionVerificationPolicy.VerifyConstructiveState(
                request.Plan, currentSnapshot, request.Plan.Definition.FormatRetentions.Select(value => value.Id),
                removedRecords, unaffectedBaseline, clock.GetUtcNow());
            issues.AddRange(intermediate.Issues);
            await AppendVerificationAsync(journal, execution, null, intermediate, CancellationToken.None).ConfigureAwait(false);
            if (!intermediate.IsVerified)
                return await FailBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                    CleanupExecutionFailureClassification.IntermediateVerification).ConfigureAwait(false);

            progress?.Report(new(CleanupExecutionProgressPhase.DestructiveGate,
                "Revalidating the destructive gate and requesting final confirmation.", completedOperations,
                execution.Graph.Operations.Count, execution.MutationStarted));
            if (cancellationToken.IsCancellationRequested)
                return await CancelBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                    completedOperations, progress).ConfigureAwait(false);
            IReadOnlyList<ExecutionIssue> backupAvailability = await backupStore.VerifyAvailableAsync(
                workspace, manifest, execution.MutationStarted ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            issues.AddRange(backupAvailability);
            LibraryScanOutcome destructiveGateScan = await scanLibrary.ScanFreshAsync(
                request.LibraryRoot, null, execution.MutationStarted ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            if (!destructiveGateScan.IsSuccess)
                issues.Add(Block("EXECUTION.DESTRUCTIVE_GATE_SCAN_FAILED", "The library could not be freshly scanned at the destructive-action gate."));
            else
            {
                ExecutionVerificationResult gateVerification = CleanupExecutionVerificationPolicy.VerifyConstructiveState(
                    request.Plan, destructiveGateScan.Snapshot!, request.Plan.Definition.FormatRetentions.Select(value => value.Id),
                    removedRecords, unaffectedBaseline, clock.GetUtcNow());
                issues.AddRange(gateVerification.Issues);
                currentSnapshot = destructiveGateScan.Snapshot!;
            }

            if (issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError))
                return await FailBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                    CleanupExecutionFailureClassification.IntermediateVerification).ConfigureAwait(false);

            bool destructiveApproved = await destructiveConfirmation.ConfirmAsync(new(
                execution.Id, request.Plan.Id, request.Plan.ContentDigest, request.Plan.Definition.TargetRecordId,
                request.Plan.Definition.RecordRemovals.Select(value => value.RecordId).ToArray(), manifest.ManifestDigest.Value),
                execution.MutationStarted ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            await AppendAsync(journal, execution, destructiveApproved ? "DestructiveGateApproved" : "DestructiveGateDeclined",
                destructiveApproved ? "Final destructive confirmation was granted." : "Final destructive confirmation was declined.",
                execution.MutationStarted ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            if (!destructiveApproved)
            {
                issues.Add(new("EXECUTION.DESTRUCTIVE_CONFIRMATION_DECLINED", ExecutionIssueSeverity.Information,
                    "The final destructive confirmation was declined."));
                return await CancelBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                    completedOperations, progress).ConfigureAwait(false);
            }

            foreach (CleanupExecutionOperation operation in execution.Graph.DestructiveOperations)
            {
                if (cancellationToken.IsCancellationRequested)
                    return await CancelBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                        completedOperations, progress).ConfigureAwait(false);
                (bool gatePassed, CalibreToolDescriptor? currentTool, LibrarySnapshot? gateSnapshot,
                    IReadOnlyList<ExecutionIssue> gateIssues) = await VerifyPerCommandGateAsync(
                    request, discovery.Tool, workspace, manifest, lease,
                    request.Plan.Definition.FormatRetentions.Select(value => value.Id),
                    removedRecords, unaffectedBaseline,
                    execution.MutationStarted ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
                issues.AddRange(gateIssues);
                if (!gatePassed)
                    return await FailBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                        CleanupExecutionFailureClassification.DestructiveCommand).ConfigureAwait(false);
                currentSnapshot = gateSnapshot!;

                if (!execution.MutationStarted)
                {
                    if (!await PersistMutationGuardAsync(execution, workspace, journal, manifest, issues).ConfigureAwait(false))
                        return await FailBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                            CleanupExecutionFailureClassification.Journal).ConfigureAwait(false);
                    await AppendMutationStartingAsync(journal, execution, CancellationToken.None).ConfigureAwait(false);
                    execution = execution.MarkMutationStarting();
                }

                CalibreBookId recordToRemove = operation.SourceRecordId
                    ?? throw new InvalidOperationException("A destructive operation has no source record.");
                execution = execution.StartOperation(operation.Id);
                progress?.Report(new(CleanupExecutionProgressPhase.Destructive,
                    $"Removing redundant record {recordToRemove.Value} through Calibre.", completedOperations,
                    execution.Graph.Operations.Count, true, operation.Id));
                await AppendOperationStartingAsync(journal, execution, operation, "remove", CancellationToken.None).ConfigureAwait(false);
                CalibreCommandResult command = await commandGateway.RemoveRecordAsync(new(
                    currentTool!, request.LibraryRoot, recordToRemove), CancellationToken.None).ConfigureAwait(false);
                await AppendCommandAsync(journal, execution, operation.Id, command, true, CancellationToken.None).ConfigureAwait(false);
                if (!command.IsSuccess)
                {
                    execution = execution.MarkOperationFailed(operation.Id, command.FailureCode ?? "EXECUTION.CALIBRE_COMMAND_FAILED",
                        CleanupExecutionFailureClassification.DestructiveCommand).RequireRecovery(CleanupExecutionFailureClassification.DestructiveCommand);
                    issues.Add(Block("EXECUTION.DESTRUCTIVE_COMMAND_FAILED", "A destructive Calibre operation failed; no later removal was attempted.", recordToRemove));
                    await CaptureFailureScanAsync(request, journal, execution, unaffectedBaseline,
                        request.Plan.Definition.FormatRetentions.Select(value => value.Id), removedRecords).ConfigureAwait(false);
                    return await FinishAsync(execution, issues, workspace, journal, manifest,
                        CleanupExecutionFailureClassification.DestructiveCommand, terminalPersisted).ConfigureAwait(false);
                }

                execution = execution.MarkOperationSucceeded(operation.Id);
                removedRecords.Add(recordToRemove);
                LibraryScanOutcome verificationScan = await scanLibrary.ScanFreshAsync(request.LibraryRoot, null, CancellationToken.None).ConfigureAwait(false);
                if (!verificationScan.IsSuccess)
                {
                    execution = execution.MarkOperationFailed(operation.Id, "EXECUTION.VERIFICATION_SCAN_FAILED",
                        CleanupExecutionFailureClassification.IntermediateVerification).RequireRecovery(CleanupExecutionFailureClassification.IntermediateVerification);
                    issues.Add(Block("EXECUTION.VERIFICATION_SCAN_FAILED", "The library could not be scanned after a destructive operation."));
                    return await FinishAsync(execution, issues, workspace, journal, manifest,
                        CleanupExecutionFailureClassification.IntermediateVerification, terminalPersisted).ConfigureAwait(false);
                }

                ExecutionVerificationResult verification = CleanupExecutionVerificationPolicy.VerifyConstructiveState(
                    request.Plan, verificationScan.Snapshot!, request.Plan.Definition.FormatRetentions.Select(value => value.Id),
                    removedRecords, unaffectedBaseline, clock.GetUtcNow());
                issues.AddRange(verification.Issues);
                await AppendVerificationAsync(journal, execution, operation.Id, verification, CancellationToken.None).ConfigureAwait(false);
                if (!verification.IsVerified)
                {
                    execution = execution.MarkOperationFailed(operation.Id, "EXECUTION.DESTRUCTIVE_VERIFICATION_FAILED",
                        CleanupExecutionFailureClassification.IntermediateVerification).RequireRecovery(CleanupExecutionFailureClassification.IntermediateVerification);
                    return await FinishAsync(execution, issues, workspace, journal, manifest,
                        CleanupExecutionFailureClassification.IntermediateVerification, terminalPersisted).ConfigureAwait(false);
                }

                execution = execution.MarkOperationVerified(operation.Id);
                currentSnapshot = verificationScan.Snapshot!;
                completedOperations++;
                await AppendOperationVerifiedAsync(journal, execution, operation, false, CancellationToken.None).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    return await CancelBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                        completedOperations, progress).ConfigureAwait(false);
            }

            execution = execution.Transition(CleanupExecutionState.Verifying);
            progress?.Report(new(CleanupExecutionProgressPhase.FinalVerification,
                "Performing final semantic verification.", completedOperations, execution.Graph.Operations.Count, true));
            LibraryScanOutcome finalScan = await scanLibrary.ScanFreshAsync(request.LibraryRoot, null, CancellationToken.None).ConfigureAwait(false);
            if (!finalScan.IsSuccess)
            {
                issues.Add(Block("EXECUTION.FINAL_SCAN_FAILED", "The library could not be read for final verification."));
                execution = execution.Transition(CleanupExecutionState.VerificationFailed)
                    .RequireRecovery(CleanupExecutionFailureClassification.FinalVerification);
                return await FinishAsync(execution, issues, workspace, journal, manifest,
                    CleanupExecutionFailureClassification.FinalVerification, terminalPersisted).ConfigureAwait(false);
            }

            ExecutionVerificationResult finalVerification = CleanupExecutionVerificationPolicy.VerifyFinalState(
                request.Plan, finalScan.Snapshot!, unaffectedBaseline, clock.GetUtcNow());
            issues.AddRange(finalVerification.Issues);
            await AppendVerificationAsync(journal, execution, null, finalVerification, CancellationToken.None).ConfigureAwait(false);
            if (!finalVerification.IsVerified)
            {
                execution = execution.Transition(CleanupExecutionState.VerificationFailed)
                    .RequireRecovery(CleanupExecutionFailureClassification.FinalVerification);
                return await FinishAsync(execution, issues, workspace, journal, manifest,
                    CleanupExecutionFailureClassification.FinalVerification, terminalPersisted).ConfigureAwait(false);
            }

            execution = execution.Transition(CleanupExecutionState.Completed);
            progress?.Report(new(CleanupExecutionProgressPhase.Completed,
                "Execution completed and the final library state was verified.", execution.Graph.Operations.Count,
                execution.Graph.Operations.Count, true));
            CleanupExecutionResult completed = await FinishAsync(execution, issues, workspace, journal, manifest,
                CleanupExecutionFailureClassification.None, terminalPersisted).ConfigureAwait(false);
            terminalPersisted = true;
            return completed;
        }
        catch (OperationCanceledException)
        {
            issues.Add(new("EXECUTION.CANCELLATION_REQUESTED", ExecutionIssueSeverity.Information,
                execution.MutationStarted
                    ? "A safe-stop request was recorded after mutation began."
                    : "Execution was cancelled before mutation."));
            return await CancelBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                execution.Operations.Count(value => value.Status is ExecutionOperationStatus.Verified or ExecutionOperationStatus.SatisfiedNoOp), progress).ConfigureAwait(false);
        }
        catch (ExecutionJournalPersistenceException)
        {
            issues.Add(Block("EXECUTION.JOURNAL_WRITE_FAILED", execution.MutationStarted
                ? "Execution journal persistence failed after mutation began; recovery is required."
                : "Execution journal persistence failed before mutation; no library change was authorized."));
            return await FailBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                CleanupExecutionFailureClassification.Journal).ConfigureAwait(false);
        }
        catch (Exception)
        {
            issues.Add(Block("EXECUTION.UNEXPECTED_FAILURE", execution.MutationStarted
                ? "An unexpected failure occurred after mutation began; recovery is required."
                : "An unexpected failure occurred before mutation; no library change was authorized."));
            return await FailBeforeOrAfterMutationAsync(execution, issues, workspace, journal, manifest,
                execution.MutationStarted ? CleanupExecutionFailureClassification.CrashOrIndeterminate : CleanupExecutionFailureClassification.Preflight).ConfigureAwait(false);
        }
        finally
        {
            if (journal is not null) await journal.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<(bool Passed, CalibreToolDescriptor? Tool, LibrarySnapshot? Snapshot,
        IReadOnlyList<ExecutionIssue> Issues)> VerifyPerCommandGateAsync(
        ExecuteCleanupPlanRequest request,
        CalibreToolDescriptor expectedTool,
        ExecutionWorkspace workspace,
        VerifiedBackupManifest manifest,
        ICleanupExecutionLeaseHandle lease,
        IEnumerable<string> processedRetentions,
        IEnumerable<CalibreBookId> removedRecords,
        Sha256Digest unaffectedBaseline,
        CancellationToken cancellationToken)
    {
        List<ExecutionIssue> issues = [];
        if (!lease.IsHeld)
            issues.Add(Block("EXECUTION.LEASE_LOST_AT_GATE", "The application execution lease is no longer held."));
        CleanupExecutionCapabilityResult capability = CleanupExecutionCapabilityPolicy.Evaluate(request.Plan);
        issues.AddRange(capability.Issues);
        if (CleanupPlanContentDigestPolicy.Compute(request.Plan.Definition) != request.Plan.ContentDigest
            || request.Plan.InputIdentity.DefinitionDigest != request.Plan.ContentDigest
            || request.Plan.Approval?.ContentDigest != request.Plan.ContentDigest)
            issues.Add(Block("EXECUTION.PLAN_CHANGED_AT_GATE", "The approved plan identity changed before a command."));
        CalibreToolDiscoveryResult tool = await toolDiscovery.DiscoverAndProbeAsync(
            request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        issues.AddRange(tool.Issues);
        if (!tool.IsSuccess || tool.Tool!.Identity != expectedTool.Identity)
            issues.Add(Block("EXECUTION.TOOL_CHANGED_AT_GATE", "The trusted Calibre executable changed before a command."));
        issues.AddRange(await backupStore.VerifyAvailableAsync(workspace, manifest, cancellationToken).ConfigureAwait(false));
        LibraryScanOutcome scan = await scanLibrary.ScanFreshAsync(
            request.LibraryRoot, null, cancellationToken).ConfigureAwait(false);
        if (!scan.IsSuccess)
            issues.Add(Block("EXECUTION.COMMAND_GATE_SCAN_FAILED",
                "A complete fresh library scan could not be proven immediately before the command."));
        else if (capability.Graph is not null)
        {
            if (!request.Confirmation.Matches(request.Plan, expectedTool.Identity,
                    workspace.CanonicalBackupDestinationIdentity, scan.Snapshot!.Identity.LibraryRoot,
                    capability.Graph.Digest))
                issues.Add(Block("EXECUTION.CONFIRMATION_CHANGED_AT_GATE",
                    "The local confirmation no longer matches the plan, graph, library, tool, or backup destination."));
            issues.AddRange(CleanupExecutionVerificationPolicy.VerifyConstructiveState(
                request.Plan, scan.Snapshot!, processedRetentions, removedRecords,
                unaffectedBaseline, clock.GetUtcNow()).Issues);
        }
        return (!issues.Any(value => value.Severity == ExecutionIssueSeverity.BlockingError),
            tool.Tool, scan.Snapshot, issues);
    }

    private async Task<bool> PersistMutationGuardAsync(
        CleanupExecution execution,
        ExecutionWorkspace workspace,
        IExecutionJournalSession journal,
        VerifiedBackupManifest manifest,
        List<ExecutionIssue> issues)
    {
        ExecutionHistoryEntry guard = new(execution.Id, execution.PlanId, execution.PlanContentDigest,
            execution.Confirmation.LibraryUuid, CleanupExecutionState.RecoveryRequired,
            CleanupExecutionDisposition.RecoveryRequired, CleanupExecutionFailureClassification.CrashOrIndeterminate,
            workspace.BundlePath, journal.JournalIdentity, manifest.ManifestDigest.Value,
            clock.GetUtcNow(), true);
        try
        {
            await historyStore.RecordAsync(guard, execution.Confirmation.CanonicalLibraryRootIdentity,
                CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            issues.Add(Block("EXECUTION.RECOVERY_GUARD_WRITE_FAILED",
                "The application-local recovery guard could not be persisted before mutation."));
            return false;
        }
    }

    private async Task<CleanupExecutionResult> CancelBeforeOrAfterMutationAsync(
        CleanupExecution execution,
        List<ExecutionIssue> issues,
        ExecutionWorkspace? workspace,
        IExecutionJournalSession? journal,
        VerifiedBackupManifest? manifest,
        int completedOperations,
        IProgress<CleanupExecutionProgress>? progress)
    {
        issues.Add(new("EXECUTION.CANCELLATION_REQUESTED", ExecutionIssueSeverity.Information,
            execution.MutationStarted ? "Safe stop was requested; no later operation was started." : "Execution was cancelled before mutation."));
        bool journalWriteFailed = false;
        if (journal is not null)
        {
            try
            {
                await AppendAsync(journal, execution, "CancellationRequested",
                    execution.MutationStarted ? "Safe stop requested after mutation boundary." : "Cancellation requested before mutation.",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                issues.Add(Block("EXECUTION.JOURNAL_WRITE_FAILED", "The cancellation boundary could not be appended to the execution journal."));
                journalWriteFailed = true;
            }
        }
        if (execution.MutationStarted)
            execution = execution.RequireRecovery(journalWriteFailed
                ? CleanupExecutionFailureClassification.Journal
                : CleanupExecutionFailureClassification.Cancellation);
        else
            execution = TransitionToCancelled(execution);
        progress?.Report(new(CleanupExecutionProgressPhase.Failed,
            execution.MutationStarted ? "Stopped at a verified safe boundary; recovery is required." : "Cancelled before mutation.",
            completedOperations, execution.Graph.Operations.Count, execution.MutationStarted));
        return await FinishAsync(execution, issues, workspace, journal, manifest,
            journalWriteFailed ? CleanupExecutionFailureClassification.Journal : CleanupExecutionFailureClassification.Cancellation,
            false).ConfigureAwait(false);
    }

    private async Task<CleanupExecutionResult> FailBeforeOrAfterMutationAsync(
        CleanupExecution execution,
        List<ExecutionIssue> issues,
        ExecutionWorkspace? workspace,
        IExecutionJournalSession? journal,
        VerifiedBackupManifest? manifest,
        CleanupExecutionFailureClassification classification)
    {
        if (execution.MutationStarted)
            execution = execution.RequireRecovery(classification);
        else
            execution = TransitionToFailureBeforeMutation(execution);
        CleanupExecutionFailureClassification terminalClassification = classification;
        if (journal is not null)
        {
            try
            {
                await AppendFailureAsync(journal, execution, "ExecutionStopped", issues, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                issues.Add(Block("EXECUTION.JOURNAL_WRITE_FAILED", "The failure boundary could not be appended to the execution journal."));
                terminalClassification = CleanupExecutionFailureClassification.Journal;
                if (execution.MutationStarted)
                    execution = execution.RequireRecovery(terminalClassification);
            }
        }

        return await FinishAsync(execution, issues, workspace, journal, manifest, terminalClassification, false).ConfigureAwait(false);
    }

    private async Task CaptureFailureScanAsync(
        ExecuteCleanupPlanRequest request,
        IExecutionJournalSession journal,
        CleanupExecution execution,
        Sha256Digest unaffectedBaseline,
        IEnumerable<string> processedRetentions,
        IEnumerable<CalibreBookId> removedRecords)
    {
        LibraryScanOutcome scan = await scanLibrary.ScanFreshAsync(request.LibraryRoot, null, CancellationToken.None).ConfigureAwait(false);
        if (!scan.IsSuccess)
        {
            await AppendAsync(journal, execution, "FailureStateScanFailed",
                "The current partial library state could not be scanned.", CancellationToken.None).ConfigureAwait(false);
            return;
        }

        ExecutionVerificationResult verification = CleanupExecutionVerificationPolicy.VerifyConstructiveState(
            request.Plan, scan.Snapshot!, processedRetentions, removedRecords, unaffectedBaseline, clock.GetUtcNow());
        await AppendVerificationAsync(journal, execution, null, verification, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<CleanupExecutionResult> FinishAsync(
        CleanupExecution execution,
        List<ExecutionIssue> issues,
        ExecutionWorkspace? workspace,
        IExecutionJournalSession? journal,
        VerifiedBackupManifest? manifest,
        CleanupExecutionFailureClassification classification,
        bool alreadyPersisted)
    {
        string? journalIdentity = journal?.JournalIdentity;
        ExecutionHistoryEntry entry = new(execution.Id, execution.PlanId, execution.PlanContentDigest,
            execution.Confirmation.LibraryUuid, execution.State, execution.Disposition,
            classification == CleanupExecutionFailureClassification.None ? execution.FailureClassification : classification,
            workspace?.BundlePath ?? string.Empty, journalIdentity ?? string.Empty,
            manifest?.ManifestDigest.Value, clock.GetUtcNow(), execution.MutationStarted);
        if (!alreadyPersisted && journal is not null)
        {
            try { await journal.CompleteAsync(entry, CancellationToken.None).ConfigureAwait(false); }
            catch
            {
                issues.Add(Block("EXECUTION.JOURNAL_TERMINAL_WRITE_FAILED", "The terminal journal record could not be proven durable."));
                if (execution.MutationStarted)
                    execution = execution.RequireRecovery(CleanupExecutionFailureClassification.Journal);
            }
        }

        try
        {
            await historyStore.RecordAsync(entry with
            {
                State = execution.State,
                Disposition = execution.Disposition,
                FailureClassification = execution.FailureClassification,
            }, execution.Confirmation.CanonicalLibraryRootIdentity, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            issues.Add(new("EXECUTION.HISTORY_INDEX_WRITE_FAILED", ExecutionIssueSeverity.Warning,
                "The primary journal remains authoritative, but the execution history index could not be updated."));
        }

        return Result(execution.Id, execution.State, execution.Disposition,
            classification == CleanupExecutionFailureClassification.None ? execution.FailureClassification : classification,
            issues, workspace?.BundlePath, journalIdentity, manifest?.ManifestDigest.Value, execution.MutationStarted);
    }

    private static CleanupExecution TransitionToCancelled(CleanupExecution execution) => execution.State switch
    {
        CleanupExecutionState.AcquiringLease or CleanupExecutionState.PreflightValidating or CleanupExecutionState.ReadyForBackup
            or CleanupExecutionState.BackingUp or CleanupExecutionState.BackupVerified or CleanupExecutionState.ReadyToExecute =>
            execution.Transition(CleanupExecutionState.CancelledBeforeMutation),
        _ => execution,
    };

    private static CleanupExecution TransitionToFailureBeforeMutation(CleanupExecution execution) => execution.State switch
    {
        CleanupExecutionState.AcquiringLease or CleanupExecutionState.PreflightValidating => execution.Transition(CleanupExecutionState.PreflightFailed),
        CleanupExecutionState.ReadyForBackup or CleanupExecutionState.BackingUp => execution.Transition(CleanupExecutionState.BackupFailed),
        CleanupExecutionState.BackupVerified or CleanupExecutionState.ReadyToExecute => execution.Transition(CleanupExecutionState.ExecutionFailedBeforeMutation),
        _ => execution,
    };

    private static bool TargetAlreadyMatches(LibrarySnapshot snapshot, CalibreBookId targetId, FormatRetentionInstruction retention) =>
        snapshot.Books.Single(value => value.Id == targetId).Formats.SingleOrDefault(value => value.Format == retention.Format)
            is { FileStatus: FormatFileStatus.Present, Fingerprint: not null } format
        && format.Fingerprint == retention.SourceState.Fingerprint;

    private async Task AppendMutationStartingAsync(IExecutionJournalSession journal, CleanupExecution execution, CancellationToken cancellationToken) =>
        await journal.AppendAsync(new("MutationStarting", execution.State, clock.GetUtcNow(),
            "The durable mutation boundary was crossed before launching the first mutating command.", MutationStarted: true), cancellationToken).ConfigureAwait(false);

    private async Task AppendOperationStartingAsync(IExecutionJournalSession journal, CleanupExecution execution,
        CleanupExecutionOperation operation, string mappedCommand, CancellationToken cancellationToken) =>
        await journal.AppendAsync(new("OperationStarting", execution.State, clock.GetUtcNow(),
            "A dependency-ready typed Calibre operation is starting.", operation.Id, mappedCommand,
            MutationStarted: execution.MutationStarted), cancellationToken).ConfigureAwait(false);

    private async Task AppendOperationVerifiedAsync(IExecutionJournalSession journal, CleanupExecution execution,
        CleanupExecutionOperation operation, bool noOp, CancellationToken cancellationToken) =>
        await journal.AppendAsync(new(noOp ? "OperationSatisfiedNoOp" : "OperationVerified", execution.State,
            clock.GetUtcNow(), noOp ? "The planned operation was satisfied without mutation." : "The operation's semantic result was verified.",
            operation.Id, MutationStarted: execution.MutationStarted), cancellationToken).ConfigureAwait(false);

    private async Task AppendCommandAsync(IExecutionJournalSession journal, CleanupExecution execution,
        ExecutionOperationId? operationId, CalibreCommandResult command, bool mutationStarted, CancellationToken cancellationToken) =>
        await journal.AppendAsync(new("CommandFinished", execution.State, clock.GetUtcNow(),
            command.IsSuccess ? "The typed Calibre command exited successfully; semantic verification is still required." : "The typed Calibre command failed.",
            operationId, command.CommandKind, command.SanitizedArguments, command.ExitCode, command.SanitizedStandardOutput,
            command.SanitizedStandardError, command.FailureCode, mutationStarted), cancellationToken).ConfigureAwait(false);

    private async Task AppendVerificationAsync(IExecutionJournalSession journal, CleanupExecution execution,
        ExecutionOperationId? operationId, ExecutionVerificationResult verification, CancellationToken cancellationToken) =>
        await journal.AppendAsync(new(verification.IsVerified ? "VerificationPassed" : "VerificationFailed", execution.State,
            clock.GetUtcNow(), verification.IsVerified ? "Fresh read-only semantic verification passed." : "Fresh read-only semantic verification failed.",
            operationId, FailureCode: verification.IsVerified ? null : verification.Issues.First(value => value.Severity == ExecutionIssueSeverity.BlockingError).Code,
            MutationStarted: execution.MutationStarted, Issues: verification.Issues), cancellationToken).ConfigureAwait(false);

    private async Task AppendFailureAsync(IExecutionJournalSession journal, CleanupExecution execution,
        string kind, IReadOnlyList<ExecutionIssue> issues, CancellationToken cancellationToken) =>
        await journal.AppendAsync(new(kind, execution.State, clock.GetUtcNow(), "Execution stopped because a fail-closed gate did not pass.",
            FailureCode: issues.LastOrDefault(value => value.Severity == ExecutionIssueSeverity.BlockingError)?.Code,
            MutationStarted: execution.MutationStarted, Issues: issues), cancellationToken).ConfigureAwait(false);

    private async Task AppendAsync(IExecutionJournalSession journal, CleanupExecution execution,
        string kind, string message, CancellationToken cancellationToken, IReadOnlyList<ExecutionIssue>? issues = null) =>
        await journal.AppendAsync(new(kind, execution.State, clock.GetUtcNow(), message,
            MutationStarted: execution.MutationStarted, Issues: issues), cancellationToken).ConfigureAwait(false);

    private static ExecutionIssue Block(string code, string explanation, CalibreBookId? recordId = null, string? format = null) =>
        new(code, ExecutionIssueSeverity.BlockingError, explanation, recordId, format);

    private static CleanupExecutionResult Result(
        CleanupExecutionId id,
        CleanupExecutionState state,
        CleanupExecutionDisposition disposition,
        CleanupExecutionFailureClassification classification,
        IReadOnlyList<ExecutionIssue> issues,
        string? bundlePath,
        string? journalIdentity,
        string? manifestDigest,
        bool mutationStarted) =>
        new(id, state, disposition, classification,
            issues.Distinct().OrderBy(value => value.Severity).ThenBy(value => value.Code, StringComparer.Ordinal).ToArray(),
            bundlePath, journalIdentity, manifestDigest, mutationStarted);
}
