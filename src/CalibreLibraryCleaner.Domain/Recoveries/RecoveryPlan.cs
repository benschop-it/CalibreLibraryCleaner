using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Recoveries;

public sealed record RecoveryPlanDefinition
{
    public RecoveryPlanDefinition(
        RecoveryInputIdentity inputIdentity,
        RecoveryProvenance provenance,
        RecoveryBackupChain originalBackupChain,
        CurrentStateReconciliation reconciliation,
        RecoveryOperationGraph operationGraph,
        ExpectedRecoveredState expectedFinalState,
        IEnumerable<RecoveryIssue> issues)
    {
        InputIdentity = inputIdentity ?? throw new ArgumentNullException(nameof(inputIdentity));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        OriginalBackupChain = originalBackupChain ?? throw new ArgumentNullException(nameof(originalBackupChain));
        Reconciliation = reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
        OperationGraph = operationGraph ?? throw new ArgumentNullException(nameof(operationGraph));
        ExpectedFinalState = expectedFinalState ?? throw new ArgumentNullException(nameof(expectedFinalState));
        Issues = new RecoveryEligibilityResult(issues, DateTimeOffset.UnixEpoch).Issues;
        if (inputIdentity.SourcePlanId != provenance.SourceCleanupPlanId
            || inputIdentity.SourceExecutionId != provenance.SourceExecutionId
            || inputIdentity.SourcePlanId != originalBackupChain.SourcePlanId
            || inputIdentity.SourceExecutionId != originalBackupChain.SourceExecutionId
            || inputIdentity.SourcePlanContentDigest != originalBackupChain.SourcePlanContentDigest
            || inputIdentity.ReconciliationVersion != reconciliation.Version
            || inputIdentity.ReconciliationDigest != reconciliation.Digest
            || inputIdentity.FullStateFingerprint != reconciliation.FullStateFingerprint
            || inputIdentity.AffectedStateFingerprint != reconciliation.AffectedStateFingerprint
            || inputIdentity.UnrelatedStateFingerprint != reconciliation.UnrelatedStateFingerprint
            || !string.Equals(inputIdentity.CurrentLibraryUuid, reconciliation.CurrentLibraryUuid, StringComparison.Ordinal)
            || inputIdentity.CurrentLibrarySchemaVersion != reconciliation.CurrentLibrarySchemaVersion)
            throw new ArgumentException("The recovery definition identities and reconciliation disagree.");
        if (originalBackupChain.RecoveryPlanContentDigest is not null)
            throw new ArgumentException("The immutable body must contain the unbound original backup-chain identity.");
        bool hasBlockingIssue = Issues.Any(value =>
            value.Severity == RecoveryIssueSeverity.Blocking);
        if (OperationGraph.Operations.Any(value =>
                value.Kind is RecoveryOperationKind.RestoreCoverFromBackup
                    or RecoveryOperationKind.RemoveRecordCreatedByExecution
                    or RecoveryOperationKind.CreateRecoveryCopyInsteadOfOverwrite
                    or RecoveryOperationKind.ManualInterventionRequired)
            && !hasBlockingIssue)
            throw new ArgumentException(
                "Unsupported or manual recovery operations require an immutable blocking issue.");
        if (hasBlockingIssue
            && OperationGraph.Operations.Any(value => value.IsDispatchable))
            throw new ArgumentException("A blocked recovery definition cannot contain dispatchable operations.");
    }

    public RecoveryInputIdentity InputIdentity { get; }
    public RecoveryProvenance Provenance { get; }
    public RecoveryBackupChain OriginalBackupChain { get; }
    public CurrentStateReconciliation Reconciliation { get; }
    public RecoveryOperationGraph OperationGraph { get; }
    public ExpectedRecoveredState ExpectedFinalState { get; }
    public IReadOnlyList<RecoveryIssue> Issues { get; }
    public IReadOnlyList<string> RequiredWarningCodes => Issues
        .Where(value => value.Severity == RecoveryIssueSeverity.AcknowledgementRequired)
        .Select(value => value.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}

public sealed record RecoveryPlanValidationResult
{
    public RecoveryPlanValidationResult(
        IEnumerable<RecoveryIssue> issues,
        DateTimeOffset validatedAtUtc,
        RecoveryInputIdentity validatedInputIdentity)
    {
        Issues = new RecoveryEligibilityResult(issues, validatedAtUtc).Issues;
        ValidatedAtUtc = validatedAtUtc.ToUniversalTime();
        ValidatedInputIdentity = validatedInputIdentity ?? throw new ArgumentNullException(nameof(validatedInputIdentity));
    }

    public IReadOnlyList<RecoveryIssue> Issues { get; }
    public DateTimeOffset ValidatedAtUtc { get; }
    public RecoveryInputIdentity ValidatedInputIdentity { get; }
    public bool IsValid => Issues.All(value => value.Severity != RecoveryIssueSeverity.Blocking);
}

public sealed record RecoveryApproval
{
    public RecoveryApproval(
        RecoveryPlanId planId,
        DateTimeOffset approvedAtUtc,
        RecoveryPlanArtifactRevision approvedRevision,
        RecoveryPlanContentDigest contentDigest,
        CleanupExecutionId sourceExecutionId,
        string currentLibraryUuid,
        Sha256Digest canonicalRootIdentityDigest,
        Sha256Digest currentStateFingerprint,
        string capabilityProfile,
        IEnumerable<string> acknowledgedWarningCodes)
    {
        PlanId = planId ?? throw new ArgumentNullException(nameof(planId));
        ArgumentException.ThrowIfNullOrWhiteSpace(currentLibraryUuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityProfile);
        ApprovedAtUtc = approvedAtUtc.ToUniversalTime();
        ApprovedRevision = approvedRevision;
        ContentDigest = contentDigest ?? throw new ArgumentNullException(nameof(contentDigest));
        SourceExecutionId = sourceExecutionId ?? throw new ArgumentNullException(nameof(sourceExecutionId));
        CurrentLibraryUuid = currentLibraryUuid.Trim();
        CanonicalRootIdentityDigest = canonicalRootIdentityDigest;
        CurrentStateFingerprint = currentStateFingerprint;
        CapabilityProfile = capabilityProfile.Trim();
        AcknowledgedWarningCodes = Array.AsReadOnly(acknowledgedWarningCodes.Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray());
    }

    public RecoveryPlanId PlanId { get; }
    public DateTimeOffset ApprovedAtUtc { get; }
    public RecoveryPlanArtifactRevision ApprovedRevision { get; }
    public RecoveryPlanContentDigest ContentDigest { get; }
    public CleanupExecutionId SourceExecutionId { get; }
    public string CurrentLibraryUuid { get; }
    public Sha256Digest CanonicalRootIdentityDigest { get; }
    public Sha256Digest CurrentStateFingerprint { get; }
    public string CapabilityProfile { get; }
    public IReadOnlyList<string> AcknowledgedWarningCodes { get; }
}

public sealed record RecoveryRevocation(
    DateTimeOffset RevokedAtUtc,
    string Reason,
    RecoveryPlanArtifactRevision? PriorApprovalRevision,
    RecoveryPlanContentDigest ContentDigest);

public sealed record RecoveryPlanLifecycleEntry(
    RecoveryPlanArtifactRevision Revision,
    RecoveryPlanState FromState,
    RecoveryPlanState ToState,
    DateTimeOffset ChangedAtUtc,
    string Reason);

public sealed record RecoveryPlanCompletion(
    RecoveryExecutionId RecoveryExecutionId,
    DateTimeOffset CompletedAtUtc,
    string RecoveryJournalFinalHash,
    bool ChangedRecordIds,
    bool PreservedUnexpectedContent);

public sealed record RecoveryPlan
{
    public RecoveryPlan(
        RecoveryPlanId id,
        RecoveryPlanSchemaVersion schemaVersion,
        RecoveryModelVersion modelVersion,
        RecoveryPolicyVersion policyVersion,
        RecoveryPlanArtifactRevision artifactRevision,
        RecoveryPlanState state,
        RecoveryPlanContentDigest contentDigest,
        DateTimeOffset createdAtUtc,
        RecoveryPlanDefinition definition,
        RecoveryPlanValidationResult validation,
        RecoveryApproval? approval,
        RecoveryRevocation? revocation,
        RecoveryPlanCompletion? completion,
        IEnumerable<RecoveryPlanLifecycleEntry> lifecycleHistory)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        SchemaVersion = schemaVersion ?? throw new ArgumentNullException(nameof(schemaVersion));
        ModelVersion = modelVersion ?? throw new ArgumentNullException(nameof(modelVersion));
        PolicyVersion = policyVersion ?? throw new ArgumentNullException(nameof(policyVersion));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        ContentDigest = contentDigest ?? throw new ArgumentNullException(nameof(contentDigest));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Validation = validation ?? throw new ArgumentNullException(nameof(validation));
        if (RecoveryPlanContentDigestPolicy.Compute(definition) != contentDigest)
            throw new ArgumentException("The recovery-plan digest does not match its immutable body.", nameof(contentDigest));
        if (definition.OriginalBackupChain.RecoveryPlanId != id
            || validation.ValidatedInputIdentity != definition.InputIdentity)
            throw new ArgumentException("The recovery-plan identity, validation, and body disagree.");
        RecoveryPlanLifecycleEntry[] history = lifecycleHistory.OrderBy(value => value.Revision.Value).ToArray();
        ValidateLifecycle(id, state, artifactRevision, contentDigest, definition, validation, approval, revocation, completion, history);
        ArtifactRevision = artifactRevision;
        State = state;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Approval = approval;
        Revocation = revocation;
        Completion = completion;
        LifecycleHistory = Array.AsReadOnly(history);
    }

    public RecoveryPlanId Id { get; }
    public RecoveryPlanSchemaVersion SchemaVersion { get; }
    public RecoveryModelVersion ModelVersion { get; }
    public RecoveryPolicyVersion PolicyVersion { get; }
    public RecoveryPlanArtifactRevision ArtifactRevision { get; }
    public RecoveryPlanState State { get; }
    public RecoveryPlanContentDigest ContentDigest { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public RecoveryPlanDefinition Definition { get; }
    public RecoveryPlanValidationResult Validation { get; }
    public RecoveryApproval? Approval { get; }
    public RecoveryRevocation? Revocation { get; }
    public RecoveryPlanCompletion? Completion { get; }
    public IReadOnlyList<RecoveryPlanLifecycleEntry> LifecycleHistory { get; }

    public static bool IsLegal(RecoveryPlanState from, RecoveryPlanState to) => (from, to) switch
    {
        (RecoveryPlanState.Draft, RecoveryPlanState.Blocked or RecoveryPlanState.Valid) => true,
        (RecoveryPlanState.Blocked, RecoveryPlanState.Draft) => true,
        (RecoveryPlanState.Valid, RecoveryPlanState.Approved or RecoveryPlanState.Blocked
            or RecoveryPlanState.Stale or RecoveryPlanState.Revoked) => true,
        (RecoveryPlanState.Approved, RecoveryPlanState.Stale or RecoveryPlanState.Revoked
            or RecoveryPlanState.Completed) => true,
        _ => false,
    };

    private static void ValidateLifecycle(
        RecoveryPlanId id,
        RecoveryPlanState state,
        RecoveryPlanArtifactRevision revision,
        RecoveryPlanContentDigest digest,
        RecoveryPlanDefinition definition,
        RecoveryPlanValidationResult validation,
        RecoveryApproval? approval,
        RecoveryRevocation? revocation,
        RecoveryPlanCompletion? completion,
        RecoveryPlanLifecycleEntry[] history)
    {
        if (history.Length == 0 || history[0].Revision.Value != 1
            || history[0].FromState != RecoveryPlanState.Draft
            || history[^1].Revision != revision || history[^1].ToState != state
            || history.Where((value, index) => value.Revision.Value != index + 1).Any()
            || history.Where((value, index) => index > 0 && value.FromState != history[index - 1].ToState).Any()
            || history.Any(value => !IsLegal(value.FromState, value.ToState)
                || string.IsNullOrWhiteSpace(value.Reason) || value.Reason.Length > 1024))
            throw new ArgumentException("Recovery-plan lifecycle history is invalid.", nameof(history));
        if (state is RecoveryPlanState.Valid or RecoveryPlanState.Approved or RecoveryPlanState.Completed
            && !validation.IsValid)
            throw new ArgumentException("A valid recovery plan cannot contain blocking validation issues.", nameof(validation));
        if (state == RecoveryPlanState.Blocked != definition.Issues.Any(value => value.Severity == RecoveryIssueSeverity.Blocking))
            throw new ArgumentException("The blocked recovery state must match the immutable blocking issues.");
        if (approval is not null)
        {
            RecoveryPlanLifecycleEntry? approvalTransition = history.SingleOrDefault(value =>
                value.ToState == RecoveryPlanState.Approved);
            if (approval.PlanId != id
                || approvalTransition is null
                || approvalTransition.Revision.Value <= 1
                || approval.ApprovedRevision.Value != approvalTransition.Revision.Value - 1
                || approval.ApprovedAtUtc != approvalTransition.ChangedAtUtc.ToUniversalTime()
                || approval.ContentDigest != digest
                || approval.SourceExecutionId != definition.InputIdentity.SourceExecutionId
                || approval.CurrentLibraryUuid != definition.InputIdentity.CurrentLibraryUuid
                || approval.CanonicalRootIdentityDigest != definition.InputIdentity.CanonicalRootIdentityDigest
                || approval.CurrentStateFingerprint != definition.InputIdentity.FullStateFingerprint
                || approval.CapabilityProfile != definition.InputIdentity.RecoveryCapabilityProfile
                || !approval.AcknowledgedWarningCodes.SequenceEqual(definition.RequiredWarningCodes))
                throw new ArgumentException("Recovery approval is not bound to the exact plan inputs and warnings.", nameof(approval));
        }
        if (state is RecoveryPlanState.Approved or RecoveryPlanState.Completed && approval is null
            || state is RecoveryPlanState.Draft or RecoveryPlanState.Blocked or RecoveryPlanState.Valid
            && approval is not null)
            throw new ArgumentException("The recovery approval does not match lifecycle state.");
        if ((state == RecoveryPlanState.Revoked) != (revocation is not null)
            || revocation is not null && (revocation.ContentDigest != digest
                || string.IsNullOrWhiteSpace(revocation.Reason)
                || revocation.PriorApprovalRevision != approval?.ApprovedRevision))
            throw new ArgumentException("Recovery revocation does not match its approval.");
        if ((state == RecoveryPlanState.Completed) != (completion is not null))
            throw new ArgumentException("Recovery completion does not match lifecycle state.");
    }
}
