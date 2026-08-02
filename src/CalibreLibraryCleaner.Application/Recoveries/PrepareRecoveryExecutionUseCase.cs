using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Recoveries;

public sealed class PrepareRecoveryExecutionUseCase(
    IRecoverySourceArtifactReader sourceReader,
    ILibraryStateSession libraryState,
    ICurrentStateReconciler reconciler,
    IRecoveryStateBackupService backupService,
    IRecoveryHistoryStore history,
    ICalibreToolDiscovery toolDiscovery,
    ICalibreExecutionProfileProvider profileProvider) : IPrepareRecoveryExecution
{
    public async Task<RecoveryExecutionPreparation> ExecuteAsync(
        PrepareRecoveryExecutionRequest request,
        IProgress<LibraryScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<RecoveryIssue> issues = [];
        if (request.Plan.State != RecoveryPlanState.Approved
            || request.Plan.Approval?.ContentDigest != request.Plan.ContentDigest
            || RecoveryPlanContentDigestPolicy.Compute(request.Plan.Definition) != request.Plan.ContentDigest)
            issues.Add(Block("RECOVERY.APPROVAL_INVALID",
                "Recovery plan", "Only an unchanged, explicitly approved recovery plan can be prepared."));

        RecoverySourceInspection source = await sourceReader.ReadAndVerifyAsync(
            request.Source.BundleIdentity, cancellationToken).ConfigureAwait(false);
        issues.AddRange(source.Issues);
        if (!source.IsVerified || !SourceMatchesPlan(source, request.Plan))
            issues.Add(Block("RECOVERY.SOURCE_CHANGED",
                "Source execution", "The original cleanup artifacts no longer match the approved recovery plan."));

        CalibreToolDiscoveryResult discovery = await toolDiscovery.DiscoverAndProbeAsync(
            request.LibraryRoot, cancellationToken).ConfigureAwait(false);
        foreach (Domain.Executions.ExecutionIssue issue in discovery.Issues)
            issues.Add(new($"RECOVERY.{issue.Code}", RecoveryIssueSeverity.Blocking,
                "Calibre capability", issue.Explanation));
        RecoveryCapabilityProfile? currentProfile = discovery.Tool is null
            ? null : profileProvider.EvaluateRecoveryProfile(discovery.Tool);
        if (!discovery.IsSuccess || discovery.Tool?.Identity != request.Tool.Identity
            || currentProfile?.ProfileIdentity != request.CapabilityProfile.ProfileIdentity
            || currentProfile?.ToolIdentity != request.CapabilityProfile.ToolIdentity)
            issues.Add(Block("RECOVERY.CAPABILITY_PROFILE_CHANGED",
                "Calibre capability", "The exact Calibre tool or recovery capability profile changed."));

        RecoveryCurrentStateScanResult scan = ProjectedRecoveryCurrentState.Create(
            libraryState.GetCurrent(request.LibraryRoot), AffectedRecordIds(request.Plan));
        issues.AddRange(scan.Issues);
        if (scan.CurrentState is null)
            return new(request.Plan, source, null, null, null, discovery.Tool,
                currentProfile, Ordered(issues));

        CurrentStateReconciliation currentReconciliation = reconciler.Reconcile(source, scan.CurrentState);
        if (currentReconciliation.Digest != request.Plan.Definition.Reconciliation.Digest
            || currentReconciliation.FullStateFingerprint != request.Plan.Definition.InputIdentity.FullStateFingerprint
            || RecoverySnapshotFingerprintPolicy.ComputeCanonicalRootIdentity(
                scan.CurrentState.Snapshot.Identity.LibraryRoot)
                != request.Plan.Definition.InputIdentity.CanonicalRootIdentityDigest)
            issues.Add(Block("RECOVERY.PLAN_STALE",
                "Current library", "The current affected state or reconciliation changed after approval."));

        bool unresolved = await history.HasUnresolvedRecoveryAsync(
            request.Plan.Definition.InputIdentity.CurrentLibraryUuid,
            request.LibraryRoot, null,
            cancellationToken).ConfigureAwait(false);
        if (unresolved)
            issues.Add(Block("RECOVERY.CONFLICTING_RECOVERY",
                "Recovery history", "Another unresolved recovery conflicts with this source execution."));

        long requiredBytes = EstimateBackupBytes(scan.CurrentState, request.Plan);
        RecoveryBackupDestinationValidation destination = await backupService.ValidateDestinationAsync(
            request.LibraryRoot, request.RecoveryBackupDestination, requiredBytes,
            cancellationToken).ConfigureAwait(false);
        issues.AddRange(destination.Issues);
        return new(request.Plan, source, scan.CurrentState,
            scan.CurrentState.Snapshot.Identity.LibraryRoot,
            destination.CanonicalDestinationIdentity, discovery.Tool, currentProfile,
            Ordered(issues));
    }

    internal static long EstimateBackupBytes(
        RecoveryCurrentStateSnapshot current,
        RecoveryPlan plan)
    {
        HashSet<CalibreBookId> affected = AffectedRecordIds(plan).ToHashSet();
        try
        {
            long formats = current.Snapshot.Books.Where(value => affected.Contains(value.Id))
                .SelectMany(value => value.Formats)
                .Aggregate(0L, (total, format) => checked(total + (format.Fingerprint?.SizeInBytes ?? 0)));
            long covers = current.Covers.Where(value => affected.Contains(value.RecordId))
                .Aggregate(0L, (total, cover) => checked(total + (cover.Fingerprint?.SizeInBytes ?? 0)));
            return checked((formats + covers) * 3 + 64L * 1024 * 1024);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    public static CalibreBookId[] AffectedRecordIds(RecoveryPlan plan) =>
        plan.Definition.Reconciliation.Records
            .SelectMany(value => new CalibreBookId?[]
            {
                value.Identity.OriginalRecordId,
                value.Identity.CurrentRecordId,
            })
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .Distinct()
            .OrderBy(value => value.Value)
            .ToArray();

    public static bool SourceMatchesPlan(RecoverySourceInspection source, RecoveryPlan plan) =>
        string.Equals(source.BundleIdentity,
            plan.Definition.Provenance.SourceBundleIdentity,
            StringComparison.OrdinalIgnoreCase)
        && source.CleanupPlan?.Id == plan.Definition.InputIdentity.SourcePlanId
        && source.CleanupPlan.SchemaVersion == plan.Definition.InputIdentity.SourcePlanSchemaVersion
        && source.CleanupPlan.ArtifactRevision == plan.Definition.InputIdentity.SourcePlanRevision
        && source.CleanupPlan.ContentDigest == plan.Definition.InputIdentity.SourcePlanContentDigest
        && source.Journal?.ExecutionId == plan.Definition.InputIdentity.SourceExecutionId
        && source.Journal.SchemaVersion == plan.Definition.InputIdentity.SourceJournalSchema
        && source.Journal.FileDigest == plan.Definition.InputIdentity.SourceJournalFileDigest
        && source.Journal.FinalEntryHash == plan.Definition.InputIdentity.SourceJournalFinalEntryHash
        && source.Journal.TerminalSummaryDigest
            == plan.Definition.InputIdentity.SourceTerminalSummaryDigest
        && source.OriginalBackupManifest?.ManifestDigest == plan.Definition.InputIdentity.OriginalManifestInternalDigest
        && source.OriginalManifestFileDigest == plan.Definition.InputIdentity.OriginalManifestFileDigest
        && source.SourceToolIdentity == plan.Definition.InputIdentity.SourceToolIdentity
        && source.SourceApplicationVersion
            == plan.Definition.InputIdentity.SourceApplicationVersion
        && source.Journal.ApplicationVersion
            == plan.Definition.InputIdentity.SourceApplicationVersion
        && RecoveryEligibilityValidator.IsSupportedSourceApplicationVersion(
            source.SourceApplicationVersion);

    private static IReadOnlyList<RecoveryIssue> Ordered(IEnumerable<RecoveryIssue> issues) =>
        new RecoveryEligibilityResult(issues, DateTimeOffset.UnixEpoch).Issues;

    private static RecoveryIssue Block(string code, string subject, string explanation) =>
        new(code, RecoveryIssueSeverity.Blocking, subject, explanation);
}
