using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Recoveries;

public sealed class ExecuteApprovedRecoveryPlanUseCase(
    IRecoverySourceArtifactReader sourceReader,
    ILibraryStateSession libraryState,
    ICurrentStateReconciler reconciler,
    IRecoveryStateBackupService backupService,
    IRecoveryJournalStore journalStore,
    IRecoveryHistoryStore historyStore,
    IRecoveryResolutionStore resolutionStore,
    ILibraryMutationLease mutationLease,
    ICalibreToolDiscovery toolDiscovery,
    ICalibreExecutionProfileProvider profileProvider,
    IRecoveryCalibreGateway commandGateway,
    IRecoveryStateVerifier verifier,
    IRecoveryIdGenerator ids,
    IDestructiveRecoveryConfirmation destructiveConfirmation,
    IClock clock) : IExecuteApprovedRecoveryPlan
{
    public async Task<RecoveryExecutionResult> ExecuteAsync(
        ExecuteRecoveryPlanRequest request,
        IProgress<RecoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RecoveryExecutionId executionId = ids.CreateExecutionId();
        List<RecoveryIssue> issues = [];
        RecoveryExecution execution;
        try
        {
            execution = RecoveryExecution.Create(executionId, request.Plan);
        }
        catch (ArgumentException)
        {
            issues.Add(Block("RECOVERY.APPROVAL_INVALID", "Recovery plan",
                "The exact immutable recovery plan is not approved."));
            return Result(executionId, RecoveryExecutionState.RecoveryFailed,
                RecoveryFailureClassification.Preflight, issues);
        }

        execution = execution.Transition(RecoveryExecutionState.PreflightValidating);
        progress?.Report(new(RecoveryProgressPhase.AcquiringLease,
            "Acquiring the shared cleanup/recovery library-mutation lease.",
            0, execution.Graph.Operations.Count, false, false));
        LibraryMutationLeaseAcquisition acquisition;
        try
        {
            acquisition = await mutationLease.TryAcquireAsync(new(executionId.ToString(),
                LibraryMutationKind.Recovery, request.LibraryRoot,
                request.Plan.Definition.InputIdentity.CurrentLibraryUuid,
                clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(executionId, RecoveryExecutionState.CancelledBeforeMutation,
                RecoveryFailureClassification.Cancellation,
                [new("RECOVERY.CANCELLED_BEFORE_MUTATION", RecoveryIssueSeverity.Information,
                    "Cancellation", "Recovery was cancelled before mutation.")]);
        }
        foreach (Domain.Executions.ExecutionIssue issue in acquisition.Issues)
            issues.Add(new($"RECOVERY.{issue.Code}", RecoveryIssueSeverity.Blocking,
                "Library lease", issue.Explanation));
        if (!acquisition.IsAcquired)
            return Result(executionId, RecoveryExecutionState.RecoveryFailed,
                RecoveryFailureClassification.Preflight, issues);

        await using ILibraryMutationLeaseHandle lease = acquisition.Lease!;
        IRecoveryJournalSession? journal = null;
        string? bundleIdentity = null;
        RecoveryCurrentStateBackup? backup = null;
        RecoveryCurrentStateSnapshot? lastVerifiedState = null;
        RecoveryOperationId? activeOperationId = null;
        bool terminalPersisted = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await historyStore.HasUnresolvedRecoveryAsync(
                    request.Plan.Definition.InputIdentity.CurrentLibraryUuid,
                    request.LibraryRoot,
                    null,
                    cancellationToken).ConfigureAwait(false))
            {
                issues.Add(Block("RECOVERY.CONFLICTING_RECOVERY", "Recovery history",
                    "Another unresolved recovery conflicts with this source execution."));
                execution = execution.MarkFailed(null, "RECOVERY.CONFLICTING_RECOVERY",
                    RecoveryFailureClassification.Preflight, false);
                return await FinishAsync(execution, issues, request.LibraryRoot, bundleIdentity, journal, backup,
                    terminalPersisted).ConfigureAwait(false);
            }

            long requiredBytes = EstimateRequiredBytes(request.Plan);
            RecoveryBackupDestinationValidation destination = await backupService.ValidateDestinationAsync(
                request.LibraryRoot, request.RecoveryBackupDestination, requiredBytes,
                cancellationToken).ConfigureAwait(false);
            issues.AddRange(destination.Issues);
            if (!destination.IsValid)
            {
                execution = execution.MarkFailed(null, "RECOVERY.BACKUP_DESTINATION_INVALID",
                    RecoveryFailureClassification.Preflight, false);
                return await FinishAsync(execution, issues, request.LibraryRoot, bundleIdentity, journal, backup,
                    terminalPersisted).ConfigureAwait(false);
            }

            RecoveryJournalReconciliationResult priorJournals =
                await journalStore.ReconcileAsync(destination.CanonicalDestinationIdentity!,
                    request.Plan.Definition.InputIdentity.CurrentLibraryUuid,
                    cancellationToken).ConfigureAwait(false);
            issues.AddRange(priorJournals.Issues);
            if (priorJournals.ManualInterventionRequired)
            {
                execution = execution.MarkFailed(null, "RECOVERY.UNRESOLVED_JOURNAL",
                    RecoveryFailureClassification.Preflight, false);
                return await FinishAsync(execution, issues, request.LibraryRoot,
                    bundleIdentity, journal, backup, terminalPersisted).ConfigureAwait(false);
            }

            bundleIdentity = await backupService.CreateWorkspaceAsync(executionId,
                destination.CanonicalDestinationIdentity!, cancellationToken).ConfigureAwait(false);
            journal = await journalStore.CreateAsync(new(executionId, request.Plan, request.Source,
                bundleIdentity, request.ApplicationVersion, clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            await AppendAsync(journal, execution, "RecoveryCreated",
                "Recovery workspace and durable journal created.", cancellationToken).ConfigureAwait(false);

            progress?.Report(new(RecoveryProgressPhase.Preflight,
                "Reverifying original artifacts, exact Calibre profile, approval, and current state.",
                0, execution.Graph.Operations.Count, false, false));
            RecoverySourceInspection source = await sourceReader.ReadAndVerifyAsync(
                request.Source.BundleIdentity, cancellationToken).ConfigureAwait(false);
            issues.AddRange(source.Issues);
            CalibreToolDiscoveryResult discovery = await toolDiscovery.DiscoverAndProbeAsync(
                request.LibraryRoot, cancellationToken).ConfigureAwait(false);
            foreach (Domain.Executions.ExecutionIssue issue in discovery.Issues)
                issues.Add(new($"RECOVERY.{issue.Code}", RecoveryIssueSeverity.Blocking,
                    "Calibre capability", issue.Explanation));
            RecoveryCapabilityProfile? profile = discovery.Tool is null
                ? null : profileProvider.EvaluateRecoveryProfile(discovery.Tool);
            if (!source.IsVerified || !PrepareRecoveryExecutionUseCase.SourceMatchesPlan(source, request.Plan))
                issues.Add(Block("RECOVERY.SOURCE_CHANGED", "Source execution",
                    "The original cleanup plan, journal, manifest, or backup artifacts changed."));
            if (!discovery.IsSuccess || discovery.Tool?.Identity != request.Tool.Identity
                || profile?.ProfileIdentity != request.CapabilityProfile.ProfileIdentity
                || profile?.ToolIdentity != request.CapabilityProfile.ToolIdentity)
                issues.Add(Block("RECOVERY.CAPABILITY_PROFILE_CHANGED", "Calibre capability",
                    "The exact Calibre executable or recovery capability profile changed."));

            RecoveryCurrentStateScanResult scan = await ScanAsync(request, execution, null,
                cancellationToken).ConfigureAwait(false);
            issues.AddRange(scan.Issues);
            if (scan.CurrentState is null)
                issues.Add(Block("RECOVERY.CURRENT_SCAN_FAILED", "Current library",
                    "The complete fresh read-only current-state scan failed."));
            else
            {
                CurrentStateReconciliation reconciliation = reconciler.Reconcile(source, scan.CurrentState);
                if (reconciliation.Digest != request.Plan.Definition.Reconciliation.Digest
                    || reconciliation.FullStateFingerprint != request.Plan.Definition.InputIdentity.FullStateFingerprint
                    || RecoverySnapshotFingerprintPolicy.ComputeCanonicalRootIdentity(
                        scan.CurrentState.Snapshot.Identity.LibraryRoot)
                        != request.Plan.Definition.InputIdentity.CanonicalRootIdentityDigest)
                    issues.Add(Block("RECOVERY.PLAN_STALE", "Current library",
                        "Current state changed after recovery-plan approval."));
                lastVerifiedState = scan.CurrentState;
            }

            Sha256Digest destructiveDigest = ComputeDestructiveDigest(request.Plan.Definition.OperationGraph);
            if (!ConfirmationMatches(request, destination.CanonicalDestinationIdentity!, destructiveDigest))
                issues.Add(Block("RECOVERY.CONFIRMATION_INVALID", "Recovery confirmation",
                    "The local confirmation is not bound to this exact plan, library, capability, backup destination, and destructive graph."));
            if (!lease.IsHeld || lease.MutationKind != LibraryMutationKind.Recovery)
                issues.Add(Block("RECOVERY.LEASE_LOST", "Library lease",
                    "The shared library-mutation lease is not held."));
            if (issues.Any(value => value.Severity == RecoveryIssueSeverity.Blocking))
            {
                execution = execution.MarkFailed(null, "RECOVERY.PREFLIGHT_FAILED",
                    RecoveryFailureClassification.Preflight, false);
                await AppendFailureAsync(journal, execution, issues, cancellationToken).ConfigureAwait(false);
                return await FinishAsync(execution, issues, request.LibraryRoot, bundleIdentity, journal, backup,
                    terminalPersisted).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            execution = execution.Transition(RecoveryExecutionState.BackingUpCurrentState);
            progress?.Report(new(RecoveryProgressPhase.CurrentStateBackup,
                "Creating and independently verifying the mandatory current affected-state backup.",
                0, execution.Graph.Operations.Count, false, false));
            await AppendAsync(journal, execution, "CurrentStateBackupStarted",
                "Current affected-state backup started before any recovery mutation.",
                cancellationToken).ConfigureAwait(false);
            RecoveryCurrentStateBackupResult backupResult = await backupService.CreateAndVerifyAsync(new(
                executionId, request.Plan, source, lastVerifiedState!, request.LibraryRoot,
                request.Tool, destination.CanonicalDestinationIdentity!, bundleIdentity, request.ApplicationVersion,
                clock.GetUtcNow(), request.Confirmation), cancellationToken).ConfigureAwait(false);
            issues.AddRange(backupResult.Issues);
            if (!backupResult.IsSuccess)
            {
                execution = execution.MarkFailed(null, "RECOVERY.CURRENT_STATE_BACKUP_FAILED",
                    RecoveryFailureClassification.CurrentStateBackup, false);
                await AppendFailureAsync(journal, execution, issues, CancellationToken.None).ConfigureAwait(false);
                return await FinishAsync(execution, issues, request.LibraryRoot, bundleIdentity, journal, backup,
                    terminalPersisted).ConfigureAwait(false);
            }

            backup = backupResult.Backup;
            execution = execution.AttachVerifiedCurrentStateBackup(
                backup!.ManifestInternalDigest.Value)
                .Transition(RecoveryExecutionState.CurrentStateBackupVerified);
            await journal.AppendAsync(new("CurrentStateBackupVerified", execution.State,
                clock.GetUtcNow(),
                "The current-state manifest and every entry were independently rehashed.",
                CurrentStateManifestFileDigest: backup.ManifestFileDigest.Value,
                CurrentStateManifestInternalDigest: backup.ManifestInternalDigest.Value),
                CancellationToken.None).ConfigureAwait(false);

            RecoveryCurrentStateScanResult postBackupScan = await ScanAsync(request, execution, null,
                cancellationToken).ConfigureAwait(false);
            issues.AddRange(postBackupScan.Issues);
            IReadOnlyList<RecoveryIssue> postBackupAvailability =
                await backupService.VerifyAvailableAsync(backup, cancellationToken).ConfigureAwait(false);
            issues.AddRange(postBackupAvailability);
            if (postBackupScan.CurrentState is null
                || postBackupScan.CurrentState.FullFingerprint != lastVerifiedState!.FullFingerprint
                || postBackupAvailability.Any(value => value.Severity == RecoveryIssueSeverity.Blocking))
            {
                issues.Add(Block("RECOVERY.STATE_CHANGED_DURING_BACKUP", "Current library",
                    "The current library or verified backup changed during backup creation."));
                execution = execution.MarkFailed(null, "RECOVERY.STATE_CHANGED_DURING_BACKUP",
                    RecoveryFailureClassification.CurrentStateBackup, false);
                return await FinishAsync(execution, issues, request.LibraryRoot, bundleIdentity, journal, backup,
                    terminalPersisted).ConfigureAwait(false);
            }
            lastVerifiedState = postBackupScan.CurrentState;

            foreach (RecoveryOperationId satisfied in SatisfyReadyNonMutating(
                         ref execution, lastVerifiedState!, false))
                await AppendNonMutatingSatisfiedAsync(
                    journal, execution, satisfied, CancellationToken.None).ConfigureAwait(false);
            RecoveryOperation[] constructive = execution.Graph.ConstructiveOperations.ToArray();
            if (constructive.Length > 0)
            {
                execution = execution.Transition(RecoveryExecutionState.RestoringConstructiveState);
                foreach (RecoveryOperation operation in constructive)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return await StopForCancellationAsync(execution, issues, request.LibraryRoot, bundleIdentity,
                            journal, backup, progress).ConfigureAwait(false);
                    RecoveryCurrentStateSnapshot? commandGateState =
                        await CommandGateAsync(request, source, profile!, lease, backup,
                            lastVerifiedState!, execution, issues,
                            cancellationToken).ConfigureAwait(false);
                    if (commandGateState is null)
                        return await FailExecutionAsync(execution, activeOperationId, issues,
                            RecoveryFailureClassification.ConstructiveCommand,
                            request.LibraryRoot, bundleIdentity, journal, backup, false).ConfigureAwait(false);
                    lastVerifiedState = commandGateState;

                    if (!execution.MutationStarted)
                    {
                        if (!await PersistMutationGuardAsync(execution, request.LibraryRoot, bundleIdentity,
                                journal, backup, issues).ConfigureAwait(false))
                            return await FailExecutionAsync(execution, null, issues,
                                RecoveryFailureClassification.JournalOrStorage,
                                request.LibraryRoot, bundleIdentity, journal, backup, true).ConfigureAwait(false);
                        await AppendAsync(journal, execution, "MutationStarting",
                            "Recovery mutation boundary crossed after both backup generations verified.",
                            CancellationToken.None, mutationStarted: true).ConfigureAwait(false);
                        execution = execution.MarkMutationStarting();
                    }

                    activeOperationId = operation.Id;
                    execution = execution.StartOperation(operation.Id);
                    progress?.Report(new(RecoveryProgressPhase.Constructive,
                        $"Applying constructive recovery operation {operation.Kind}.",
                        Completed(execution), execution.Graph.Operations.Count, true, false, operation.Id));
                    await AppendOperationStartingAsync(journal, execution, operation,
                        CancellationToken.None).ConfigureAwait(false);
                    RecoveryCalibreCommandResult command = await DispatchAsync(
                        request, profile!, execution, operation, source, lastVerifiedState!,
                        CancellationToken.None).ConfigureAwait(false);
                    await AppendCommandAsync(journal, execution, operation, command,
                        CancellationToken.None).ConfigureAwait(false);
                    if (!command.IsTransportSuccess)
                    {
                        await MarkStateUncertainAsync(request.LibraryRoot, "RECOVERY_CONSTRUCTIVE_COMMAND_FAILED",
                            "Calibre did not return an unambiguous successful constructive recovery result.",
                            operation.Id.Value).ConfigureAwait(false);
                        issues.Add(Block("RECOVERY.CONSTRUCTIVE_COMMAND_FAILED",
                            $"Operation {operation.Id}", "The constructive Calibre command failed."));
                        await AddFailureRescanIssuesAsync(
                            request, execution, issues, CancellationToken.None).ConfigureAwait(false);
                        return await FailExecutionAsync(execution, operation.Id, issues,
                            RecoveryFailureClassification.ConstructiveCommand,
                            request.LibraryRoot, bundleIdentity, journal, backup, false).ConfigureAwait(false);
                    }

                    execution = execution.MarkCommandSucceeded(operation.Id);
                    LibraryStateSessionOutcome deltaOutcome = await ApplyRecoveryDeltaAsync(
                        request, execution, operation, command).ConfigureAwait(false);
                    if (!deltaOutcome.IsSuccess)
                    {
                        issues.Add(Block("RECOVERY.DELTA_COMMIT_FAILED", $"Operation {operation.Id}",
                            deltaOutcome.Explanation ?? "The successful recovery command could not be durably projected."));
                        return await FailExecutionAsync(execution, operation.Id, issues,
                            RecoveryFailureClassification.ConstructiveVerification,
                            request.LibraryRoot, bundleIdentity, journal, backup, false).ConfigureAwait(false);
                    }
                    RecoveryCurrentStateScanResult after = await ScanAsync(request, execution,
                        command.CreatedRecordId, CancellationToken.None).ConfigureAwait(false);
                    issues.AddRange(after.Issues);
                    if (after.CurrentState is null)
                    {
                        issues.Add(Block("RECOVERY.POST_COMMAND_SCAN_FAILED",
                            $"Operation {operation.Id}", "Fresh semantic verification could not scan the library."));
                        return await FailExecutionAsync(execution, operation.Id, issues,
                            RecoveryFailureClassification.ConstructiveVerification,
                            request.LibraryRoot, bundleIdentity, journal, backup, false).ConfigureAwait(false);
                    }
                    RecoveryIssue? verificationIssue = VerifyOperation(
                        request.Plan, operation, lastVerifiedState!, after.CurrentState,
                        execution, command, out RecoveryRecordIdMapping? mapping);
                    if (verificationIssue is not null)
                    {
                        issues.Add(verificationIssue);
                        return await FailExecutionAsync(execution, operation.Id, issues,
                            RecoveryFailureClassification.ConstructiveVerification,
                            request.LibraryRoot, bundleIdentity, journal, backup, false).ConfigureAwait(false);
                    }
                    if (mapping is not null)
                    {
                        execution = execution.AddRecordIdMapping(mapping);
                        await journal.AppendAsync(new("RecordIdMapped", execution.State,
                            clock.GetUtcNow(), "A created Calibre record was uniquely mapped to its logical recovery identity.",
                            operation.Id, MutationStarted: true, RecordIdMapping: mapping),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    RecoveryExecution verifiedExecution = execution.MarkOperationVerified(operation.Id,
                        $"constructive:{operation.Id.Value}");
                    if (operation.Kind == RecoveryOperationKind.RestoreFormatFromBackup
                        && operation.Format is not null)
                        verifiedExecution = verifiedExecution.RecordRestoredFormat(
                            operation.LogicalRecordId, operation.Format);
                    await AppendVerificationAsync(journal, verifiedExecution, operation,
                        CancellationToken.None).ConfigureAwait(false);
                    execution = verifiedExecution;
                    lastVerifiedState = after.CurrentState;
                    activeOperationId = null;
                    if (cancellationToken.IsCancellationRequested)
                        return await StopForCancellationAsync(execution, issues, request.LibraryRoot, bundleIdentity,
                            journal, backup, progress).ConfigureAwait(false);
                }
            }

            execution = execution.Transition(RecoveryExecutionState.VerifyingConstructiveState);
            await AppendAsync(journal, execution, "IntermediateVerificationStarted",
                "Fresh intermediate semantic verification started before any destructive recovery.",
                CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new(RecoveryProgressPhase.IntermediateVerification,
                "Performing fresh intermediate semantic verification before destructive recovery.",
                Completed(execution), execution.Graph.Operations.Count,
                execution.MutationStarted, false));
            RecoveryCurrentStateScanResult intermediateScan = await ScanAsync(
                request, execution, null, execution.MutationStarted
                    ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
            issues.AddRange(intermediateScan.Issues);
            if (intermediateScan.CurrentState is null
                || !VerifyConstructiveAndPreserved(request.Plan, execution,
                    intermediateScan.CurrentState, issues))
            {
                return await FailExecutionAsync(execution, null, issues,
                    RecoveryFailureClassification.ConstructiveVerification,
                    request.LibraryRoot, bundleIdentity, journal, backup, false).ConfigureAwait(false);
            }
            lastVerifiedState = intermediateScan.CurrentState;
            foreach (RecoveryOperationId satisfied in SatisfyReadyNonMutating(
                         ref execution, intermediateScan.CurrentState, true))
                await AppendNonMutatingSatisfiedAsync(
                    journal, execution, satisfied, CancellationToken.None).ConfigureAwait(false);

            RecoveryOperation[] destructive = execution.Graph.DestructiveOperations.ToArray();
            if (destructive.Length > 0)
            {
                execution = execution.Transition(RecoveryExecutionState.ReadyForDestructiveRecovery);
                progress?.Report(new(RecoveryProgressPhase.DestructiveGate,
                    "Intermediate verification passed; requesting separate destructive confirmation.",
                    Completed(execution), execution.Graph.Operations.Count,
                    execution.MutationStarted, false));
                bool approved = await destructiveConfirmation.ConfirmAsync(new(execution.Id,
                    request.Plan.Id, request.Plan.ContentDigest, destructiveDigest,
                    destructive, backup.ManifestFileDigest,
                    backup.ManifestInternalDigest),
                    execution.MutationStarted ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
                await journal.AppendAsync(new(
                    approved ? "DestructiveRecoveryApproved" : "DestructiveRecoveryDeclined",
                    execution.State, clock.GetUtcNow(),
                    approved
                        ? "The exact destructive operation digest was explicitly confirmed."
                        : "Destructive recovery confirmation was declined.",
                    MutationStarted: execution.MutationStarted,
                    DestructiveRecoveryStarted: execution.DestructiveRecoveryStarted,
                    DestructiveOperationDigest: destructiveDigest.Value),
                    CancellationToken.None).ConfigureAwait(false);
                if (!approved)
                    return await StopForCancellationAsync(execution, issues, request.LibraryRoot, bundleIdentity,
                        journal, backup, progress).ConfigureAwait(false);

                if (!execution.MutationStarted)
                {
                    if (!await PersistMutationGuardAsync(execution, request.LibraryRoot, bundleIdentity,
                            journal, backup, issues).ConfigureAwait(false))
                        return await FailExecutionAsync(execution, null, issues,
                            RecoveryFailureClassification.JournalOrStorage,
                            request.LibraryRoot, bundleIdentity, journal, backup, true).ConfigureAwait(false);
                    await AppendAsync(journal, execution, "MutationStarting",
                        "Recovery mutation boundary crossed after both backup generations verified.",
                        CancellationToken.None, mutationStarted: true).ConfigureAwait(false);
                    execution = execution.MarkMutationStarting();
                }
                await AppendAsync(journal, execution, "DestructiveRecoveryStarting",
                    "Destructive recovery begins only after constructive verification.",
                    CancellationToken.None, mutationStarted: true,
                    destructiveStarted: true).ConfigureAwait(false);
                execution = execution.BeginDestructiveRecovery();

                foreach (RecoveryOperation operation in destructive)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return await StopForCancellationAsync(execution, issues, request.LibraryRoot, bundleIdentity,
                            journal, backup, progress).ConfigureAwait(false);
                    RecoveryCurrentStateSnapshot? commandGateState =
                        await CommandGateAsync(request, source, profile!, lease, backup,
                            lastVerifiedState!, execution, issues,
                            CancellationToken.None).ConfigureAwait(false);
                    if (commandGateState is null)
                        return await FailExecutionAsync(execution, activeOperationId, issues,
                            RecoveryFailureClassification.DestructiveCommand,
                            request.LibraryRoot, bundleIdentity, journal, backup, true).ConfigureAwait(false);
                    lastVerifiedState = commandGateState;
                    RecoveryIssue? targetIssue = ValidateDestructiveTarget(
                        request.Plan, operation, execution, lastVerifiedState!);
                    if (targetIssue is not null)
                    {
                        issues.Add(targetIssue);
                        return await FailExecutionAsync(execution, null, issues,
                            RecoveryFailureClassification.DestructiveCommand,
                            request.LibraryRoot, bundleIdentity, journal, backup, true).ConfigureAwait(false);
                    }
                    activeOperationId = operation.Id;
                    execution = execution.StartOperation(operation.Id);
                    progress?.Report(new(RecoveryProgressPhase.Destructive,
                        $"Applying destructive recovery operation {operation.Kind} last.",
                        Completed(execution), execution.Graph.Operations.Count, true, true, operation.Id));
                    await AppendOperationStartingAsync(journal, execution, operation,
                        CancellationToken.None).ConfigureAwait(false);
                    RecoveryCalibreCommandResult command = await DispatchAsync(request, profile!,
                        execution, operation, source, lastVerifiedState!,
                        CancellationToken.None).ConfigureAwait(false);
                    await AppendCommandAsync(journal, execution, operation, command,
                        CancellationToken.None).ConfigureAwait(false);
                    if (!command.IsTransportSuccess)
                    {
                        await MarkStateUncertainAsync(request.LibraryRoot, "RECOVERY_DESTRUCTIVE_COMMAND_FAILED",
                            "Calibre did not return an unambiguous successful destructive recovery result.",
                            operation.Id.Value).ConfigureAwait(false);
                        issues.Add(Block("RECOVERY.DESTRUCTIVE_COMMAND_FAILED",
                            $"Operation {operation.Id}",
                            "A destructive recovery command failed; no later operation was attempted."));
                        await AddFailureRescanIssuesAsync(
                            request, execution, issues, CancellationToken.None).ConfigureAwait(false);
                        return await FailExecutionAsync(execution, operation.Id, issues,
                            RecoveryFailureClassification.DestructiveCommand,
                            request.LibraryRoot, bundleIdentity, journal, backup, true).ConfigureAwait(false);
                    }
                    execution = execution.MarkCommandSucceeded(operation.Id);
                    LibraryStateSessionOutcome deltaOutcome = await ApplyRecoveryDeltaAsync(
                        request, execution, operation, command).ConfigureAwait(false);
                    if (!deltaOutcome.IsSuccess)
                    {
                        issues.Add(Block("RECOVERY.DELTA_COMMIT_FAILED", $"Operation {operation.Id}",
                            deltaOutcome.Explanation ?? "The successful destructive recovery command could not be durably projected."));
                        return await FailExecutionAsync(execution, operation.Id, issues,
                            RecoveryFailureClassification.DestructiveCommand,
                            request.LibraryRoot, bundleIdentity, journal, backup, true).ConfigureAwait(false);
                    }
                    RecoveryCurrentStateScanResult after = await ScanAsync(request, execution,
                        null, CancellationToken.None).ConfigureAwait(false);
                    issues.AddRange(after.Issues);
                    if (after.CurrentState is null)
                    {
                        issues.Add(Block("RECOVERY.POST_DESTRUCTIVE_SCAN_FAILED",
                            $"Operation {operation.Id}", "The destructive command effect is uncertain."));
                        return await FailExecutionAsync(execution, operation.Id, issues,
                            RecoveryFailureClassification.DestructiveCommand,
                            request.LibraryRoot, bundleIdentity, journal, backup, true).ConfigureAwait(false);
                    }
                    RecoveryIssue? verificationIssue = VerifyOperation(
                        request.Plan, operation, lastVerifiedState!, after.CurrentState,
                        execution, command, out _);
                    if (verificationIssue is not null)
                    {
                        issues.Add(verificationIssue);
                        return await FailExecutionAsync(execution, operation.Id, issues,
                            RecoveryFailureClassification.DestructiveCommand,
                            request.LibraryRoot, bundleIdentity, journal, backup, true).ConfigureAwait(false);
                    }
                    RecoveryExecution verifiedExecution = execution.MarkOperationVerified(operation.Id,
                        $"destructive:{operation.Id.Value}");
                    await AppendVerificationAsync(journal, verifiedExecution, operation,
                        CancellationToken.None).ConfigureAwait(false);
                    execution = verifiedExecution;
                    lastVerifiedState = after.CurrentState;
                    activeOperationId = null;
                    if (cancellationToken.IsCancellationRequested)
                        return await StopForCancellationAsync(execution, issues, request.LibraryRoot, bundleIdentity,
                            journal, backup, progress).ConfigureAwait(false);
                }
            }

            if (execution.State == RecoveryExecutionState.VerifyingConstructiveState)
                execution = execution.Transition(RecoveryExecutionState.FinalVerifying);
            else if (execution.State == RecoveryExecutionState.ReadyForDestructiveRecovery)
                execution = execution.Transition(RecoveryExecutionState.FinalVerifying);
            else if (execution.State == RecoveryExecutionState.ApplyingDestructiveRecovery)
                execution = execution.Transition(RecoveryExecutionState.FinalVerifying);
            await AppendAsync(journal, execution, "FinalVerificationStarted",
                "Fresh final semantic verification started.",
                CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new(RecoveryProgressPhase.FinalVerification,
                "Performing final fresh semantic verification and backup-chain checks.",
                Completed(execution), execution.Graph.Operations.Count,
                execution.MutationStarted, execution.DestructiveRecoveryStarted));
            RecoveryCurrentStateScanResult finalScan = await ScanAsync(
                request, execution, null, CancellationToken.None).ConfigureAwait(false);
            issues.AddRange(finalScan.Issues);
            IReadOnlyList<RecoveryIssue> backupIssues = await backupService.VerifyAvailableAsync(
                backup, CancellationToken.None).ConfigureAwait(false);
            issues.AddRange(backupIssues);
            RecoverySourceInspection finalSource = await sourceReader.ReadAndVerifyAsync(
                source.BundleIdentity, CancellationToken.None).ConfigureAwait(false);
            issues.AddRange(finalSource.Issues);
            if (!finalSource.IsVerified
                || !PrepareRecoveryExecutionUseCase.SourceMatchesPlan(
                    finalSource, request.Plan))
                issues.Add(Block("RECOVERY.FINAL_SOURCE_CHAIN_CHANGED",
                    "Final verification",
                    "The original cleanup plan, journal, manifest, or backup chain changed before recovery completed."));
            if (finalScan.CurrentState is null)
            {
                issues.Add(Block("RECOVERY.FINAL_SCAN_FAILED", "Final verification",
                    "The recovered library could not be read."));
                RecoveryExecution finalExecution = execution.MarkFinalVerification(
                    false, execution.DestructiveRecoveryStarted, "final-scan-failed");
                await AppendAsync(journal, finalExecution, "FinalVerificationFailed",
                    "Final semantic verification could not establish the recovered state.",
                    CancellationToken.None).ConfigureAwait(false);
                execution = finalExecution;
            }
            else
            {
                VerifyConstructiveAndPreserved(
                    request.Plan, execution, finalScan.CurrentState, issues);
                RecoveryVerificationResult verification = verifier.Verify(
                    request.Plan.Definition.ExpectedFinalState,
                    finalScan.CurrentState.Snapshot, execution.RecordIdMappings,
                    clock.GetUtcNow());
                issues.AddRange(verification.Issues);
                if (finalScan.CurrentState.UnrelatedFingerprint
                        != request.Plan.Definition.ExpectedFinalState.UnrelatedStateFingerprint)
                    issues.Add(Block("RECOVERY.UNRELATED_STATE_CHANGED", "Final verification",
                        "Library records outside the recovery scope changed during recovery."));
                bool passed = verification.IsVerified
                    && !issues.Any(value => value.Severity == RecoveryIssueSeverity.Blocking);
                RecoveryExecution finalExecution = execution.MarkFinalVerification(passed, !passed
                    && execution.DestructiveRecoveryStarted, passed
                    ? "final-semantic-verification-passed"
                    : "final-semantic-verification-failed");
                await AppendAsync(journal, finalExecution,
                    passed ? "FinalVerificationPassed" : "FinalVerificationFailed",
                    passed
                        ? "Final semantic verification and both backup chains passed."
                        : "Final semantic verification or a backup-chain check failed.",
                    CancellationToken.None).ConfigureAwait(false);
                execution = finalExecution;
                if (passed)
                {
                    foreach (RecoveryRecordIdMapping mapping in
                             execution.RecordIdMappings.ToArray())
                    {
                        ExpectedRecordState expectedRecord =
                            request.Plan.Definition.ExpectedFinalState.Records
                                .Single(value =>
                                    value.LogicalRecordId
                                    == mapping.LogicalRecordId).OriginalState;
                        execution = execution.FinalizeRecordIdMapping(
                            mapping.LogicalRecordId,
                            expectedRecord.Formats.Select(value => value.Format),
                            expectedRecord.Identifiers.Select(value =>
                                $"{value.Type}:{value.Value}"));
                        RecoveryRecordIdMapping finalized =
                            execution.RecordIdMappings.Single(value =>
                                value.LogicalRecordId == mapping.LogicalRecordId);
                        await journal.AppendAsync(new(
                            "RecordIdMappingFinalized", execution.State,
                            clock.GetUtcNow(),
                            "The semantic record-ID mapping was finalized only after complete final verification.",
                            MutationStarted: execution.MutationStarted,
                            DestructiveRecoveryStarted:
                                execution.DestructiveRecoveryStarted,
                            RecordIdMapping: finalized),
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }

            RecoveryExecutionResult result = await FinishAsync(execution, issues, request.LibraryRoot, bundleIdentity,
                journal, backup, terminalPersisted).ConfigureAwait(false);
            terminalPersisted = true;
            if (result.State == RecoveryExecutionState.Recovered)
            {
                try
                {
                    await resolutionStore.RecordResolutionAsync(new(
                        request.Plan.Definition.InputIdentity.SourceExecutionId,
                        request.Plan.Id, execution.Id,
                        request.Plan.Definition.InputIdentity.CurrentLibraryUuid,
                        journal.JournalIdentity, journal.FinalEntryHash
                            ?? throw new InvalidOperationException(
                                "The terminal recovery journal hash is unavailable."),
                        clock.GetUtcNow(), result.State,
                        backup.ManifestFileDigest.Value,
                        backup.ManifestInternalDigest.Value,
                        request.Plan.Definition.InputIdentity.CanonicalRootIdentityDigest.Value,
                        request.Plan.Definition.InputIdentity.SourceJournalFileDigest.Value,
                        request.Plan.Definition.InputIdentity.OriginalManifestFileDigest.Value),
                        request.LibraryRoot, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    issues.Add(new("RECOVERY.RESOLUTION_INDEX_WRITE_FAILED",
                        RecoveryIssueSeverity.AcknowledgementRequired,
                        "Recovery resolution",
                        "Semantic recovery is verified in the primary journal, but the secondary source-resolution index could not be updated."));
                    result = result with { Issues = Ordered(issues) };
                }
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return await StopForCancellationAsync(execution, issues, request.LibraryRoot, bundleIdentity,
                journal, backup, progress).ConfigureAwait(false);
        }
        catch (Exception)
        {
            issues.Add(Block("RECOVERY.UNEXPECTED_FAILURE", "Recovery execution",
                execution.MutationStarted
                    ? "An unexpected post-mutation failure requires manual intervention."
                    : "An unexpected pre-mutation failure prevented recovery."));
            if (execution.MutationStarted)
            {
                try
                {
                    await AddFailureRescanIssuesAsync(
                        request, execution, issues, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    issues.Add(Block("RECOVERY.FAILURE_STATE_SCAN_FAILED",
                        "Failure-state reconciliation",
                        "The current library state could not be established after an unexpected recovery failure."));
                }
            }
            return await FailExecutionAsync(execution, activeOperationId, issues,
                execution.MutationStarted
                    ? RecoveryFailureClassification.CrashOrIndeterminate
                    : RecoveryFailureClassification.Preflight,
                request.LibraryRoot, bundleIdentity, journal, backup, execution.MutationStarted)
                .ConfigureAwait(false);
        }
        finally
        {
            if (journal is not null) await journal.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<RecoveryCurrentStateSnapshot?> CommandGateAsync(
        ExecuteRecoveryPlanRequest request,
        RecoverySourceInspection expectedSource,
        RecoveryCapabilityProfile expectedProfile,
        ILibraryMutationLeaseHandle lease,
        RecoveryCurrentStateBackup backup,
        RecoveryCurrentStateSnapshot expectedState,
        RecoveryExecution execution,
        List<RecoveryIssue> issues,
        CancellationToken cancellationToken)
    {
        if (!lease.IsHeld || lease.MutationKind != LibraryMutationKind.Recovery)
            issues.Add(Block("RECOVERY.LEASE_LOST_AT_GATE", "Command gate",
                "The shared library-mutation lease is no longer held."));
        if (RecoveryPlanContentDigestPolicy.Compute(request.Plan.Definition) != request.Plan.ContentDigest
            || request.Plan.Approval?.ContentDigest != request.Plan.ContentDigest)
            issues.Add(Block("RECOVERY.PLAN_CHANGED_AT_GATE", "Command gate",
                "The approved recovery plan changed before a command."));
        RecoverySourceInspection source = await sourceReader.ReadAndVerifyAsync(
            expectedSource.BundleIdentity, cancellationToken).ConfigureAwait(false);
        if (!source.IsVerified || !PrepareRecoveryExecutionUseCase.SourceMatchesPlan(source, request.Plan))
            issues.Add(Block("RECOVERY.SOURCE_CHANGED_AT_GATE", "Command gate",
                "The original backup chain changed before a command."));
        CalibreToolDiscoveryResult discovery = await toolDiscovery.DiscoverAndProbeAsync(
            request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        RecoveryCapabilityProfile? profile = discovery.Tool is null
            ? null : profileProvider.EvaluateRecoveryProfile(discovery.Tool);
        if (!discovery.IsSuccess || discovery.Tool?.Identity != request.Tool.Identity
            || profile?.ProfileIdentity != expectedProfile.ProfileIdentity)
            issues.Add(Block("RECOVERY.TOOL_CHANGED_AT_GATE", "Command gate",
                "The exact Calibre executable or capability profile changed."));
        issues.AddRange(await backupService.VerifyAvailableAsync(backup, cancellationToken).ConfigureAwait(false));
        RecoveryCurrentStateScanResult scan = await ScanAsync(
            request, execution, null, cancellationToken).ConfigureAwait(false);
        issues.AddRange(scan.Issues);
        if (scan.CurrentState is null || scan.CurrentState.FullFingerprint != expectedState.FullFingerprint)
            issues.Add(Block("RECOVERY.CURRENT_STATE_CHANGED_AT_GATE", "Command gate",
                "Actual current library state changed after the last verified safe boundary."));
        return issues.Any(value => value.Severity == RecoveryIssueSeverity.Blocking)
            ? null : scan.CurrentState;
    }

    private async Task<RecoveryCalibreCommandResult> DispatchAsync(
        ExecuteRecoveryPlanRequest request,
        RecoveryCapabilityProfile profile,
        RecoveryExecution execution,
        RecoveryOperation operation,
        RecoverySourceInspection source,
        RecoveryCurrentStateSnapshot before,
        CancellationToken cancellationToken)
    {
        ExpectedRecordState expected = request.Plan.Definition.ExpectedFinalState.Records
            .Single(value => value.LogicalRecordId == operation.LogicalRecordId).OriginalState;
        return operation.Kind switch
        {
            RecoveryOperationKind.CreateRecoveredRecord =>
                await commandGateway.CreateEmptyRecordAsync(new(request.Tool, profile,
                    request.LibraryRoot, expected.Title,
                    expected.Authors.Select(value => value.Name).ToArray(),
                    expected.AuthorSort), cancellationToken).ConfigureAwait(false),
            RecoveryOperationKind.RestoreMetadataFromBackup =>
                await commandGateway.SetMetadataFieldAsync(new(request.Tool, profile,
                    request.LibraryRoot, ResolveCurrentRecordId(operation, execution),
                    ParseMetadataField(operation.RequiredCapability),
                    MetadataValues(ParseMetadataField(operation.RequiredCapability), expected)),
                    cancellationToken).ConfigureAwait(false),
            RecoveryOperationKind.RestoreFormatFromBackup =>
                before.Snapshot.Books.SingleOrDefault(value =>
                        value.Id == ResolveCurrentRecordId(operation, execution))?
                    .Formats.Any(value => value.Format == operation.Format) == true
                    ? new(operation.Kind.ToString(), false, null, [], string.Empty,
                        string.Empty, TimeSpan.Zero,
                        FailureCode: "RECOVERY.UNEXPECTED_FORMAT_OVERWRITE_BLOCKED")
                    : await commandGateway.AddOrReplaceFormatAsync(new(request.Tool, profile,
                    request.LibraryRoot, ResolveCurrentRecordId(operation, execution),
                    operation.Format!,
                    FindOriginalArtifact(source, operation).PhysicalIdentity,
                    operation.OriginalBackupFingerprint!,
                    false),
                    cancellationToken).ConfigureAwait(false),
            RecoveryOperationKind.RemoveFormatAddedByExecution =>
                await commandGateway.RemoveFormatAsync(new(request.Tool, profile,
                    request.LibraryRoot, ResolveCurrentRecordId(operation, execution),
                    operation.Format!,
                    operation.ExpectedCurrentFingerprint!),
                    cancellationToken).ConfigureAwait(false),
            RecoveryOperationKind.RemoveRecordCreatedByExecution =>
                await commandGateway.RemoveRecordAsync(new(request.Tool, profile,
                    request.LibraryRoot, ResolveCurrentRecordId(operation, execution)),
                    cancellationToken).ConfigureAwait(false),
            _ => new(operation.Kind.ToString(), false, null, [], string.Empty,
                string.Empty, TimeSpan.Zero, FailureCode: "RECOVERY.OPERATION_NOT_DISPATCHABLE"),
        };
    }

    private RecoveryIssue? VerifyOperation(
        RecoveryPlan plan,
        RecoveryOperation operation,
        RecoveryCurrentStateSnapshot before,
        RecoveryCurrentStateSnapshot after,
        RecoveryExecution execution,
        RecoveryCalibreCommandResult command,
        out RecoveryRecordIdMapping? mapping)
    {
        mapping = null;
        ExpectedRecoveredRecordState expected = plan.Definition.ExpectedFinalState.Records
            .Single(value => value.LogicalRecordId == operation.LogicalRecordId);
        if (operation.Kind == RecoveryOperationKind.CreateRecoveredRecord)
        {
            HashSet<CalibreBookId> priorIds = before.Snapshot.Books.Select(value => value.Id).ToHashSet();
            CalibreBook[] candidates = after.Snapshot.Books.Where(value => !priorIds.Contains(value.Id))
                .Where(value => value.Title == expected.OriginalState.Title
                    && value.Authors.Select(author => author.Name)
                        .SequenceEqual(expected.OriginalState.Authors.Select(author => author.Name)))
                .ToArray();
            if (command.CreatedRecordId is not null)
                candidates = candidates.Where(value => value.Id == command.CreatedRecordId).ToArray();
            if (candidates.Length != 1)
                return Block("RECOVERY.CREATED_RECORD_AMBIGUOUS", $"Operation {operation.Id}",
                    "The created record could not be uniquely rediscovered by scan delta and semantic facts.");
            CalibreBook created = candidates[0];
            CalibreBookId? cleanupTarget = plan.Definition.Reconciliation.SourceOperations
                .Where(value => value.SourceRecordId == operation.OriginalRecordId)
                .Select(value => (CalibreBookId?)value.TargetRecordId)
                .Distinct().SingleOrDefault();
            mapping = new(operation.LogicalRecordId, operation.OriginalRecordId,
                cleanupTarget ?? operation.CurrentRecordId, created.Id, [],
                created.Identifiers.Select(value => $"{value.Type}:{value.Value}"),
                clock.GetUtcNow());
            return null;
        }

        CalibreBookId target = ResolveCurrentRecordId(operation, execution);
        CalibreBook? current = after.Snapshot.Books.SingleOrDefault(value => value.Id == target);
        if (operation.Kind == RecoveryOperationKind.RestoreMetadataFromBackup)
        {
            if (current is null || !MetadataFieldMatches(
                    ParseMetadataField(operation.RequiredCapability), expected.OriginalState, current))
                return Block("RECOVERY.METADATA_VERIFICATION_FAILED", $"Operation {operation.Id}",
                    "The supported metadata field does not match verified pre-state after process success.");
            return null;
        }
        if (operation.Kind == RecoveryOperationKind.RestoreFormatFromBackup)
        {
            if (current?.Formats.SingleOrDefault(value => value.Format == operation.Format)
                is not { FileStatus: FormatFileStatus.Present or FormatFileStatus.ProjectedPresent, Fingerprint: not null } format
                || format.Fingerprint != operation.OriginalBackupFingerprint)
                return Block("RECOVERY.FORMAT_VERIFICATION_FAILED", $"Operation {operation.Id}",
                    "The restored format hash does not match the original verified backup.");
            return null;
        }
        if (operation.Kind == RecoveryOperationKind.RemoveFormatAddedByExecution)
        {
            if (current is null || current.Formats.Any(value => value.Format == operation.Format))
                return Block("RECOVERY.FORMAT_REMOVAL_VERIFICATION_FAILED", $"Operation {operation.Id}",
                    "The explicitly approved cleanup-only format remains after process success.");
            return null;
        }
        if (operation.Kind == RecoveryOperationKind.RemoveRecordCreatedByExecution
            && current is not null)
            return Block("RECOVERY.RECORD_REMOVAL_VERIFICATION_FAILED", $"Operation {operation.Id}",
                "The explicitly approved cleanup-created record remains after process success.");
        return null;
    }

    private static bool VerifyConstructiveAndPreserved(
        RecoveryPlan plan,
        RecoveryExecution execution,
        RecoveryCurrentStateSnapshot state,
        List<RecoveryIssue> issues)
    {
        foreach (RecoveryOperation operation in execution.Graph.ConstructiveOperations)
        {
            if (execution.Operations.Single(value => value.Operation.Id == operation.Id).Status
                != RecoveryOperationStatus.Verified)
                issues.Add(Block("RECOVERY.CONSTRUCTIVE_OPERATION_UNVERIFIED",
                    $"Operation {operation.Id}",
                    "A constructive recovery operation is not durably verified."));
        }
        foreach (PreservedContentExpectation preserved in plan.Definition.ExpectedFinalState.PreservedContent)
        {
            if (state.Snapshot.Books.SingleOrDefault(value => value.Id == preserved.CurrentRecordId)?
                    .Formats.SingleOrDefault(value => value.Format == preserved.Format)
                is not { FileStatus: FormatFileStatus.Present or FormatFileStatus.ProjectedPresent, Fingerprint: not null } format
                || format.Fingerprint != preserved.Fingerprint)
                issues.Add(Block("RECOVERY.PRESERVED_CONTENT_CHANGED",
                    $"Logical record {preserved.LogicalRecordId} / {preserved.Format}",
                    "Unexpected current content selected for preservation changed or disappeared."));
        }
        foreach (ReconciledRecoveryRecord preservedRecord in plan.Definition.Reconciliation.Records
                     .Where(value => value.Identity.CurrentRecordId is not null))
        {
            CalibreBook? actual = state.Snapshot.Books.SingleOrDefault(value =>
                value.Id == preservedRecord.Identity.CurrentRecordId!.Value);
            if (actual is null
                || RecoveryRecordFingerprintPolicy.Compute(actual)
                    != preservedRecord.Identity.CurrentMetadataFingerprint)
                issues.Add(Block("RECOVERY.PRESERVED_METADATA_CHANGED",
                    $"Logical record {preservedRecord.Identity.LogicalRecordId}",
                    "Affected-record metadata changed or disappeared after the approved current-state backup."));
        }
        if (state.UnrelatedFingerprint != plan.Definition.ExpectedFinalState.UnrelatedStateFingerprint)
            issues.Add(Block("RECOVERY.UNRELATED_STATE_CHANGED", "Intermediate verification",
                "Library state outside the recovery scope changed."));
        return issues.All(value => value.Severity != RecoveryIssueSeverity.Blocking);
    }

    private async Task<LibraryStateSessionOutcome> ApplyRecoveryDeltaAsync(
        ExecuteRecoveryPlanRequest request,
        RecoveryExecution execution,
        RecoveryOperation operation,
        RecoveryCalibreCommandResult command)
    {
        LibraryState current = libraryState.GetCurrent(request.LibraryRoot)
            ?? throw new InvalidOperationException("Authoritative projected state disappeared during recovery.");
        DateTimeOffset appliedAt = clock.GetUtcNow().ToUniversalTime();
        if (appliedAt < current.ProjectedAtUtc) appliedAt = current.ProjectedAtUtc;
        ExpectedRecordState expected = request.Plan.Definition.ExpectedFinalState.Records
            .Single(value => value.LogicalRecordId == operation.LogicalRecordId).OriginalState;
        LibraryStateDelta? delta = operation.Kind switch
        {
            RecoveryOperationKind.CreateRecoveredRecord when command.CreatedRecordId is not null =>
                new CreateRecordLibraryStateDelta(current.GenerationId, current.Revision,
                    operation.Id.Value, appliedAt, command.CreatedRecordId.Value,
                    expected.Title, expected.Authors.Select(value => value.Name), expected.AuthorSort),
            RecoveryOperationKind.RestoreMetadataFromBackup =>
                new SetMetadataLibraryStateDelta(current.GenerationId, current.Revision,
                    operation.Id.Value, appliedAt, ResolveCurrentRecordId(operation, execution),
                    ToLibraryMetadataField(ParseMetadataField(operation.RequiredCapability)),
                    MetadataValues(ParseMetadataField(operation.RequiredCapability), expected)),
            RecoveryOperationKind.RestoreFormatFromBackup =>
                new AddOrReplaceFormatLibraryStateDelta(current.GenerationId, current.Revision,
                    operation.Id.Value, appliedAt, ResolveCurrentRecordId(operation, execution),
                    operation.Format!, operation.OriginalBackupFingerprint!, null),
            RecoveryOperationKind.RemoveFormatAddedByExecution =>
                new RemoveFormatLibraryStateDelta(current.GenerationId, current.Revision,
                    operation.Id.Value, appliedAt, ResolveCurrentRecordId(operation, execution),
                    operation.Format!, operation.ExpectedCurrentFingerprint!),
            RecoveryOperationKind.RemoveRecordCreatedByExecution =>
                new RemoveRecordWithContentLibraryStateDelta(current.GenerationId, current.Revision,
                    operation.Id.Value, appliedAt, ResolveCurrentRecordId(operation, execution)),
            _ => null,
        };
        if (operation.Kind == RecoveryOperationKind.CreateRecoveredRecord && command.CreatedRecordId is null)
        {
            await MarkStateUncertainAsync(request.LibraryRoot, "RECOVERY_CREATED_ID_MISSING",
                "Calibre reported record creation success without one deterministic created record ID.",
                operation.Id.Value).ConfigureAwait(false);
            return LibraryStateSessionOutcome.Failure("LIBRARY_STATE.CREATED_ID_MISSING",
                "A successful create-record command did not return a deterministic record ID.");
        }
        if (delta is null)
            return LibraryStateSessionOutcome.Failure("LIBRARY_STATE.DELTA_UNSUPPORTED",
                "The recovery operation has no projected-state delta mapping.");
        return await libraryState.ApplyAsync(request.LibraryRoot, delta, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private Task<LibraryStateSessionOutcome> MarkStateUncertainAsync(
        string libraryRoot,
        string code,
        string explanation,
        string operationId) => libraryState.MarkUncertainAsync(libraryRoot,
        new(code, explanation, clock.GetUtcNow(), operationId), CancellationToken.None);

    private static LibraryMetadataField ToLibraryMetadataField(
        RecoveryCalibreMetadataField field) => field switch
        {
            RecoveryCalibreMetadataField.Title => LibraryMetadataField.Title,
            RecoveryCalibreMetadataField.Authors => LibraryMetadataField.Authors,
            RecoveryCalibreMetadataField.AuthorSort => LibraryMetadataField.AuthorSort,
            RecoveryCalibreMetadataField.Publisher => LibraryMetadataField.Publisher,
            RecoveryCalibreMetadataField.PublicationDate => LibraryMetadataField.PublicationDate,
            RecoveryCalibreMetadataField.Languages => LibraryMetadataField.Languages,
            RecoveryCalibreMetadataField.Identifiers => LibraryMetadataField.Identifiers,
            RecoveryCalibreMetadataField.Series => LibraryMetadataField.Series,
            RecoveryCalibreMetadataField.SeriesIndex => LibraryMetadataField.SeriesIndex,
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

    private Task<RecoveryCurrentStateScanResult> ScanAsync(
        ExecuteRecoveryPlanRequest request,
        RecoveryExecution execution,
        CalibreBookId? additionalId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<CalibreBookId> affected =
            PrepareRecoveryExecutionUseCase.AffectedRecordIds(request.Plan).ToHashSet();
        affected.UnionWith(execution.RecordIdMappings.Select(value => value.RecoveredRecordId));
        if (additionalId is not null) affected.Add(additionalId.Value);
        return Task.FromResult(ProjectedRecoveryCurrentState.Create(
            libraryState.GetCurrent(request.LibraryRoot), affected));
    }

    private async Task AddFailureRescanIssuesAsync(
        ExecuteRecoveryPlanRequest request,
        RecoveryExecution execution,
        List<RecoveryIssue> issues,
        CancellationToken cancellationToken)
    {
        RecoveryCurrentStateScanResult scan = await ScanAsync(
            request, execution, null, cancellationToken).ConfigureAwait(false);
        issues.AddRange(scan.Issues);
        if (scan.CurrentState is null)
            issues.Add(Block("RECOVERY.FAILURE_STATE_SCAN_FAILED",
                "Failure-state reconciliation",
                "The current library state could not be established after a failed recovery command."));
    }

    private async Task<bool> PersistMutationGuardAsync(
        RecoveryExecution execution,
        string libraryRoot,
        string bundleIdentity,
        IRecoveryJournalSession journal,
        RecoveryCurrentStateBackup backup,
        List<RecoveryIssue> issues)
    {
        try
        {
            await historyStore.RecordAsync(new(execution.Id, execution.PlanId,
                execution.PlanContentDigest,
                execution.SourceExecutionId,
                execution.LibraryUuid, RecoveryExecutionState.ManualInterventionRequired,
                RecoveryFailureClassification.CrashOrIndeterminate,
                bundleIdentity, journal.JournalIdentity, backup.ManifestInternalDigest.Value,
                clock.GetUtcNow(), true, execution.DestructiveRecoveryStarted,
                execution.ChangedRecordIds, execution.PreservedUnexpectedContent,
                backup.ManifestFileDigest.Value,
                backup.CanonicalRootIdentityDigest?.Value,
                backup.SourceJournalFileDigest?.Value,
                backup.OriginalManifestFileDigest?.Value),
                libraryRoot, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch
        {
            issues.Add(Block("RECOVERY.GUARD_WRITE_FAILED", "Recovery journal",
                "The application-local recovery guard could not be persisted before mutation."));
            return false;
        }
    }

    private async Task<RecoveryExecutionResult> StopForCancellationAsync(
        RecoveryExecution execution,
        List<RecoveryIssue> issues,
        string libraryRoot,
        string? bundleIdentity,
        IRecoveryJournalSession? journal,
        RecoveryCurrentStateBackup? backup,
        IProgress<RecoveryProgress>? progress)
    {
        issues.Add(new("RECOVERY.CANCELLATION_REQUESTED", RecoveryIssueSeverity.Information,
            "Cancellation", execution.MutationStarted
                ? "Recovery stopped at the next verified safe operation boundary."
                : "Recovery was cancelled before mutation."));
        if (journal is not null)
            await AppendAsync(journal, execution, "CancellationRequested",
                "Cancellation was latched and honored at a verified safe boundary.",
                CancellationToken.None).ConfigureAwait(false);
        if (!execution.MutationStarted)
            execution = execution.Transition(RecoveryExecutionState.CancelledBeforeMutation);
        else
            execution = execution.MarkFailed(null, "RECOVERY.CANCELLED_AT_SAFE_BOUNDARY",
                RecoveryFailureClassification.Cancellation,
                execution.DestructiveRecoveryStarted);
        progress?.Report(new(RecoveryProgressPhase.Stopped,
            "Recovery stopped at a safe boundary; completed recovery operations were not reversed.",
            Completed(execution), execution.Graph.Operations.Count,
            execution.MutationStarted, execution.DestructiveRecoveryStarted));
        return await FinishAsync(execution, issues, libraryRoot, bundleIdentity, journal, backup, false)
            .ConfigureAwait(false);
    }

    private async Task<RecoveryExecutionResult> FailExecutionAsync(
        RecoveryExecution execution,
        RecoveryOperationId? activeOperationId,
        List<RecoveryIssue> issues,
        RecoveryFailureClassification classification,
        string libraryRoot,
        string? bundleIdentity,
        IRecoveryJournalSession? journal,
        RecoveryCurrentStateBackup? backup,
        bool manual)
    {
        execution = execution.MarkFailed(activeOperationId,
            issues.LastOrDefault(value => value.Severity == RecoveryIssueSeverity.Blocking)?.Code
                ?? "RECOVERY.EXECUTION_FAILED",
            classification, manual);
        if (journal is not null)
        {
            try
            {
                await AppendFailureAsync(journal, execution, issues,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                issues.Add(Block("RECOVERY.JOURNAL_WRITE_FAILED", "Recovery journal",
                    "The post-mutation journal boundary could not be persisted."));
                execution = execution.MarkFailed(null, "RECOVERY.JOURNAL_WRITE_FAILED",
                    RecoveryFailureClassification.JournalOrStorage,
                    execution.MutationStarted);
            }
        }
        return await FinishAsync(execution, issues, libraryRoot, bundleIdentity, journal, backup, false)
            .ConfigureAwait(false);
    }

    private async Task<RecoveryExecutionResult> FinishAsync(
        RecoveryExecution execution,
        List<RecoveryIssue> issues,
        string libraryRoot,
        string? bundleIdentity,
        IRecoveryJournalSession? journal,
        RecoveryCurrentStateBackup? backup,
        bool alreadyPersisted)
    {
        RecoveryHistoryEntry entry = new(execution.Id, execution.PlanId,
            execution.PlanContentDigest,
            execution.SourceExecutionId,
            execution.LibraryUuid, execution.State, execution.FailureClassification,
            bundleIdentity ?? string.Empty, journal?.JournalIdentity ?? string.Empty,
            backup?.ManifestInternalDigest.Value, clock.GetUtcNow(),
            execution.MutationStarted, execution.DestructiveRecoveryStarted,
            execution.ChangedRecordIds, execution.PreservedUnexpectedContent,
            backup?.ManifestFileDigest.Value,
            backup?.CanonicalRootIdentityDigest?.Value,
            backup?.SourceJournalFileDigest?.Value,
            backup?.OriginalManifestFileDigest?.Value);
        if (!alreadyPersisted && journal is not null)
        {
            try { await journal.CompleteAsync(entry, CancellationToken.None).ConfigureAwait(false); }
            catch
            {
                issues.Add(Block("RECOVERY.TERMINAL_SUMMARY_WRITE_FAILED",
                    "Recovery journal", "The terminal recovery journal and summary could not be proven durable."));
                execution = execution.MarkFailed(null,
                    "RECOVERY.TERMINAL_SUMMARY_WRITE_FAILED",
                    RecoveryFailureClassification.JournalOrStorage,
                    execution.MutationStarted);
            }
        }
        try
        {
            if (journal is not null || execution.MutationStarted)
            {
                await historyStore.RecordAsync(entry with
                {
                    State = execution.State,
                    FailureClassification = execution.FailureClassification,
                    ChangedRecordIds = execution.ChangedRecordIds,
                    PreservedUnexpectedContent = execution.PreservedUnexpectedContent,
                }, libraryRoot, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            issues.Add(new("RECOVERY.HISTORY_INDEX_WRITE_FAILED",
                RecoveryIssueSeverity.AcknowledgementRequired, "Recovery history",
                "The primary recovery journal remains authoritative, but its secondary history index could not be updated."));
        }
        return new(execution.Id, execution.State, execution.FailureClassification,
            Ordered(issues), execution.RecordIdMappings, bundleIdentity,
            journal?.JournalIdentity, backup?.ManifestInternalDigest.Value,
            execution.MutationStarted, execution.DestructiveRecoveryStarted,
            execution.SemanticPreStateRestored, execution.PreservedUnexpectedContent);
    }

    private static bool ConfirmationMatches(
        ExecuteRecoveryPlanRequest request,
        string canonicalDestination,
        Sha256Digest destructiveDigest)
    {
        RecoveryExecutionConfirmation value = request.Confirmation;
        RecoveryInputIdentity input = request.Plan.Definition.InputIdentity;
        return value.PlanId == request.Plan.Id
            && value.PlanRevision == request.Plan.ArtifactRevision
            && value.PlanContentDigest == request.Plan.ContentDigest
            && value.SourceExecutionId == input.SourceExecutionId
            && value.LibraryUuid == input.CurrentLibraryUuid
            && value.CanonicalRootIdentityDigest == input.CanonicalRootIdentityDigest
            && value.CurrentStateFingerprint == input.FullStateFingerprint
            && value.CapabilityProfile == input.RecoveryCapabilityProfile
            && string.Equals(value.CurrentStateBackupDestinationIdentity,
                canonicalDestination, StringComparison.OrdinalIgnoreCase)
            && value.DestructiveOperationDigest == destructiveDigest
            && value.OtherMutatorsClosed && value.SafeBoundaryCancellationUnderstood;
    }

    public static Sha256Digest ComputeDestructiveDigest(RecoveryOperationGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return RecoveryOperationGraph.ComputeCanonicalDigest(graph.DestructiveOperations);
    }

    private static List<RecoveryOperationId> SatisfyReadyNonMutating(
        ref RecoveryExecution execution,
        RecoveryCurrentStateSnapshot state,
        bool intermediateVerified)
    {
        List<RecoveryOperationId> satisfied = [];
        bool changed;
        do
        {
            changed = false;
            foreach (RecoveryOperationProgress progress in execution.Operations
                         .Where(value => value.Operation.Phase == RecoveryOperationPhase.NonMutating
                             && value.Status == RecoveryOperationStatus.Planned).ToArray())
            {
                if (!CanSatisfyNonMutating(
                        progress.Operation, execution, state, intermediateVerified))
                    continue;
                try
                {
                    execution = execution.SatisfyNonMutating(progress.Operation.Id);
                    satisfied.Add(progress.Operation.Id);
                    changed = true;
                }
                catch (InvalidOperationException)
                {
                    // A dependency is not verified yet; the next phase will revisit it.
                }
            }
        } while (changed);
        return satisfied;
    }

    private static bool CanSatisfyNonMutating(
        RecoveryOperation operation,
        RecoveryExecution execution,
        RecoveryCurrentStateSnapshot state,
        bool intermediateVerified)
    {
        if (operation.Id.Value == "verify:intermediate-barrier")
            return intermediateVerified;
        RecoveryVerificationExpectation expectation = operation.Verification;
        if (expectation.Format is null) return true;
        CalibreBookId recordId;
        try { recordId = ResolveCurrentRecordId(operation, execution); }
        catch (InvalidOperationException) { return false; }
        BookFormat? format = state.Snapshot.Books.SingleOrDefault(value => value.Id == recordId)?
            .Formats.SingleOrDefault(value => value.Format == expectation.Format);
        return expectation.ExpectedPresent
            ? format is { FileStatus: FormatFileStatus.Present or FormatFileStatus.ProjectedPresent, Fingerprint: not null }
                && (expectation.ExpectedFingerprint is null
                    || format.Fingerprint == expectation.ExpectedFingerprint)
            : format is null;
    }

    private static RecoveryIssue? ValidateDestructiveTarget(
        RecoveryPlan plan,
        RecoveryOperation operation,
        RecoveryExecution execution,
        RecoveryCurrentStateSnapshot state)
    {
        if (operation.Kind == RecoveryOperationKind.RemoveRecordCreatedByExecution)
            return Block("RECOVERY.RECORD_REMOVAL_IDENTITY_INCOMPLETE",
                $"Operation {operation.Id}",
                "The destructive record target has no exact approved current semantic identity.");
        if (operation.Kind != RecoveryOperationKind.RemoveFormatAddedByExecution)
            return Block("RECOVERY.DESTRUCTIVE_OPERATION_UNSUPPORTED",
                $"Operation {operation.Id}", "The destructive operation is not allow-listed.");
        CalibreBookId recordId = ResolveCurrentRecordId(operation, execution);
        CalibreBook? record = state.Snapshot.Books.SingleOrDefault(value => value.Id == recordId);
        ReconciledRecoveryRecord identity = plan.Definition.Reconciliation.Records.Single(value =>
            value.Identity.LogicalRecordId == operation.LogicalRecordId);
        BookFormat? format = record?.Formats.SingleOrDefault(value =>
            value.Format == operation.Format);
        if (record is null
            || RecoveryRecordFingerprintPolicy.Compute(record)
                != identity.Identity.MetadataFingerprint
            || format is not
            { FileStatus: FormatFileStatus.Present or FormatFileStatus.ProjectedPresent, Fingerprint: not null }
            || operation.ExpectedCurrentFingerprint is null
            || format.Fingerprint != operation.ExpectedCurrentFingerprint)
            return Block("RECOVERY.DESTRUCTIVE_TARGET_CHANGED",
                $"Operation {operation.Id}",
                "The exact semantic record or format bytes no longer match the approved destructive target.");
        return null;
    }

    private static CalibreBookId ResolveCurrentRecordId(
        RecoveryOperation operation,
        RecoveryExecution execution) =>
        execution.RecordIdMappings.SingleOrDefault(value =>
            value.LogicalRecordId == operation.LogicalRecordId)?.RecoveredRecordId
        ?? operation.CurrentRecordId
        ?? throw new InvalidOperationException("The recovery target record has not been uniquely mapped.");

    private static VerifiedRecoverySourceArtifact FindOriginalArtifact(
        RecoverySourceInspection source,
        RecoveryOperation operation) =>
        source.Artifacts.Single(value => value.RelativePath == operation.OriginalBackupArtifactIdentity
            && value.Kind == RecoverySourceArtifactKind.OriginalRawFormat);

    private static RecoveryCalibreMetadataField ParseMetadataField(string capability) =>
        Enum.Parse<RecoveryCapability>(capability, false) switch
        {
            RecoveryCapability.RestoreTitle => RecoveryCalibreMetadataField.Title,
            RecoveryCapability.RestoreAuthors => RecoveryCalibreMetadataField.Authors,
            RecoveryCapability.RestoreAuthorSort => RecoveryCalibreMetadataField.AuthorSort,
            RecoveryCapability.RestorePublisher => RecoveryCalibreMetadataField.Publisher,
            RecoveryCapability.RestorePublicationDate => RecoveryCalibreMetadataField.PublicationDate,
            RecoveryCapability.RestoreLanguages => RecoveryCalibreMetadataField.Languages,
            RecoveryCapability.RestoreIdentifiers => RecoveryCalibreMetadataField.Identifiers,
            RecoveryCapability.RestoreSeries => RecoveryCalibreMetadataField.Series,
            RecoveryCapability.RestoreSeriesIndex => RecoveryCalibreMetadataField.SeriesIndex,
            _ => throw new InvalidOperationException("The metadata operation has no qualified field mapping."),
        };

    private static string[] MetadataValues(
        RecoveryCalibreMetadataField field,
        ExpectedRecordState expected) => field switch
        {
            RecoveryCalibreMetadataField.Title => [expected.Title],
            RecoveryCalibreMetadataField.Authors => expected.Authors.Select(value => value.Name).ToArray(),
            RecoveryCalibreMetadataField.AuthorSort => [expected.AuthorSort],
            RecoveryCalibreMetadataField.Publisher => [expected.Publisher ?? string.Empty],
            RecoveryCalibreMetadataField.PublicationDate =>
                [expected.PublicationDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty],
            RecoveryCalibreMetadataField.Languages => expected.Languages.ToArray(),
            RecoveryCalibreMetadataField.Identifiers => expected.Identifiers
                .Select(value => $"{value.Type}:{value.Value}").ToArray(),
            RecoveryCalibreMetadataField.Series => [expected.Series ?? string.Empty],
            RecoveryCalibreMetadataField.SeriesIndex =>
                [expected.SeriesIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty],
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

    private static bool MetadataFieldMatches(
        RecoveryCalibreMetadataField field,
        ExpectedRecordState expected,
        CalibreBook actual) => field switch
        {
            RecoveryCalibreMetadataField.Title => expected.Title == actual.Title,
            RecoveryCalibreMetadataField.Authors => expected.Authors.Select(value => value.Name)
                .SequenceEqual(actual.Authors.Select(value => value.Name)),
            RecoveryCalibreMetadataField.AuthorSort => expected.AuthorSort == actual.AuthorSort,
            RecoveryCalibreMetadataField.Publisher => expected.Publisher == actual.PublicationMetadata.Publisher,
            RecoveryCalibreMetadataField.PublicationDate => expected.PublicationDate == actual.PublicationMetadata.PublicationDate,
            RecoveryCalibreMetadataField.Languages => expected.Languages.SequenceEqual(actual.PublicationMetadata.Languages),
            RecoveryCalibreMetadataField.Identifiers => expected.Identifiers.SequenceEqual(
                actual.Identifiers.OrderBy(value => value.Type, StringComparer.Ordinal)
                    .ThenBy(value => value.Value, StringComparer.Ordinal)
                    .Select(value => new ExpectedIdentifierState(value.Type, value.Value))),
            RecoveryCalibreMetadataField.Series => expected.Series == actual.PublicationMetadata.Series,
            RecoveryCalibreMetadataField.SeriesIndex => expected.SeriesIndex == actual.PublicationMetadata.SeriesIndex,
            _ => false,
        };

    private static int Completed(RecoveryExecution execution) => execution.Operations.Count(value =>
        value.Status is RecoveryOperationStatus.Verified or RecoveryOperationStatus.SatisfiedNoAction);

    private static long EstimateRequiredBytes(RecoveryPlan plan)
    {
        try
        {
            long formats = plan.Definition.Reconciliation.Records.SelectMany(value => value.Formats)
                .Where(value => value.CurrentFingerprint is not null)
                .Aggregate(0L, (total, format) => checked(total + format.CurrentFingerprint!.SizeInBytes));
            return checked(formats * 3 + 64L * 1024 * 1024);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private async Task AppendOperationStartingAsync(
        IRecoveryJournalSession journal,
        RecoveryExecution execution,
        RecoveryOperation operation,
        CancellationToken cancellationToken) =>
        await journal.AppendAsync(new("OperationStarting", execution.State, clock.GetUtcNow(),
            "A dependency-ready typed Calibre recovery operation is starting.",
            operation.Id, operation.RequiredCapability, MutationStarted: execution.MutationStarted,
            DestructiveRecoveryStarted: execution.DestructiveRecoveryStarted),
            cancellationToken).ConfigureAwait(false);

    private async Task AppendCommandAsync(
        IRecoveryJournalSession journal,
        RecoveryExecution execution,
        RecoveryOperation operation,
        RecoveryCalibreCommandResult command,
        CancellationToken cancellationToken) =>
        await journal.AppendAsync(new("CommandFinished", execution.State, clock.GetUtcNow(),
            command.IsTransportSuccess
                ? "The typed Calibre command exited successfully; semantic verification is still required."
                : "The typed Calibre command failed.",
            operation.Id, command.CommandKind, command.SanitizedArguments,
            command.ExitCode, command.SanitizedStandardOutput,
            command.SanitizedStandardError, command.FailureCode,
            execution.MutationStarted, execution.DestructiveRecoveryStarted),
            cancellationToken).ConfigureAwait(false);

    private async Task AppendVerificationAsync(
        IRecoveryJournalSession journal,
        RecoveryExecution execution,
        RecoveryOperation operation,
        CancellationToken cancellationToken) =>
        await journal.AppendAsync(new("OperationVerified", execution.State, clock.GetUtcNow(),
            "Fresh read-only semantic verification passed before dependent recovery work.",
            operation.Id, MutationStarted: execution.MutationStarted,
            DestructiveRecoveryStarted: execution.DestructiveRecoveryStarted),
            cancellationToken).ConfigureAwait(false);

    private async Task AppendNonMutatingSatisfiedAsync(
        IRecoveryJournalSession journal,
        RecoveryExecution execution,
        RecoveryOperationId operationId,
        CancellationToken cancellationToken) =>
        await journal.AppendAsync(new("OperationSatisfiedNoAction", execution.State,
            clock.GetUtcNow(),
            "The non-mutating recovery expectation was proven from a fresh scan.",
            operationId, MutationStarted: execution.MutationStarted,
            DestructiveRecoveryStarted: execution.DestructiveRecoveryStarted),
            cancellationToken).ConfigureAwait(false);

    private async Task AppendFailureAsync(
        IRecoveryJournalSession journal,
        RecoveryExecution execution,
        IReadOnlyList<RecoveryIssue> issues,
        CancellationToken cancellationToken) =>
        await journal.AppendAsync(new("RecoveryStopped", execution.State, clock.GetUtcNow(),
            "Recovery stopped at a fail-closed boundary.",
            execution.Operations.SingleOrDefault(value =>
                value.Status == RecoveryOperationStatus.Failed)?.Operation.Id,
            FailureCode: issues.LastOrDefault(value => value.Severity == RecoveryIssueSeverity.Blocking)?.Code,
            MutationStarted: execution.MutationStarted,
            DestructiveRecoveryStarted: execution.DestructiveRecoveryStarted,
            Issues: issues), cancellationToken).ConfigureAwait(false);

    private async Task AppendAsync(
        IRecoveryJournalSession journal,
        RecoveryExecution execution,
        string kind,
        string message,
        CancellationToken cancellationToken,
        bool? mutationStarted = null,
        bool? destructiveStarted = null) =>
        await journal.AppendAsync(new(kind, execution.State, clock.GetUtcNow(), message,
            MutationStarted: mutationStarted ?? execution.MutationStarted,
            DestructiveRecoveryStarted: destructiveStarted ?? execution.DestructiveRecoveryStarted),
            cancellationToken).ConfigureAwait(false);

    private static IReadOnlyList<RecoveryIssue> Ordered(IEnumerable<RecoveryIssue> issues) =>
        new RecoveryEligibilityResult(issues, DateTimeOffset.UnixEpoch).Issues;

    private static RecoveryIssue Block(string code, string subject, string explanation) =>
        new(code, RecoveryIssueSeverity.Blocking, subject, explanation);

    private static RecoveryExecutionResult Result(
        RecoveryExecutionId id,
        RecoveryExecutionState state,
        RecoveryFailureClassification classification,
        IReadOnlyList<RecoveryIssue> issues) =>
        new(id, state, classification, issues, [], null, null, null,
            false, false, false, false);
}
