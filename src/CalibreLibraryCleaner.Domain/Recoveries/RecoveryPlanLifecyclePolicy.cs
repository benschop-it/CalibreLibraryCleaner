namespace CalibreLibraryCleaner.Domain.Recoveries;

public static class RecoveryPlanLifecyclePolicy
{
    public static RecoveryPlan Create(
        RecoveryPlanId id,
        RecoveryPlanDefinition definition,
        RecoveryPlanValidationResult validation,
        DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(validation);
        RecoveryPlanContentDigest digest = RecoveryPlanContentDigestPolicy.Compute(definition);
        RecoveryPlanState initial = validation.IsValid && !definition.Issues.Any(value => value.Severity == RecoveryIssueSeverity.Blocking)
            ? RecoveryPlanState.Valid
            : RecoveryPlanState.Blocked;
        return new(id, RecoveryPlanSchemaVersion.V1, RecoveryModelVersion.V1, RecoveryPolicyVersion.V1,
            new RecoveryPlanArtifactRevision(1), initial, digest, atUtc, definition, validation,
            null, null, null,
            [new(new RecoveryPlanArtifactRevision(1), RecoveryPlanState.Draft, initial, atUtc.ToUniversalTime(),
                initial == RecoveryPlanState.Valid
                    ? "Deterministic recovery-plan validation passed."
                    : "Recovery-plan generation found blocking issues.")]);
    }

    public static RecoveryPlan Approve(
        RecoveryPlan plan,
        IEnumerable<string> acknowledgedWarningCodes,
        DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.State != RecoveryPlanState.Valid || !plan.Validation.IsValid)
            throw new InvalidOperationException("Only a current valid recovery plan can be approved.");
        string[] acknowledgements = acknowledgedWarningCodes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!acknowledgements.SequenceEqual(plan.Definition.RequiredWarningCodes))
            throw new InvalidOperationException("Every immutable recovery warning must be acknowledged exactly.");
        RecoveryPlanArtifactRevision revision = new(plan.ArtifactRevision.Value + 1);
        RecoveryApproval approval = new(plan.Id, atUtc, plan.ArtifactRevision, plan.ContentDigest,
            plan.Definition.InputIdentity.SourceExecutionId, plan.Definition.InputIdentity.CurrentLibraryUuid,
            plan.Definition.InputIdentity.CanonicalRootIdentityDigest,
            plan.Definition.InputIdentity.FullStateFingerprint,
            plan.Definition.InputIdentity.RecoveryCapabilityProfile, acknowledgements);
        return Transition(plan, revision, RecoveryPlanState.Approved, atUtc,
            "Explicit local approval of the canonical immutable recovery body and all warnings.",
            plan.Validation, approval, null, null);
    }

    public static RecoveryPlan MarkStale(
        RecoveryPlan plan,
        RecoveryPlanValidationResult validation,
        DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.State is not (RecoveryPlanState.Valid or RecoveryPlanState.Approved)) return plan;
        return Transition(plan, new(plan.ArtifactRevision.Value + 1), RecoveryPlanState.Stale, atUtc,
            "A bound source artifact, capability, reconciliation, or current-state fingerprint changed.",
            validation, plan.Approval, null, null);
    }

    public static RecoveryPlan Revoke(RecoveryPlan plan, string reason, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (plan.State is not (RecoveryPlanState.Valid or RecoveryPlanState.Approved))
            throw new InvalidOperationException("Only a valid or approved recovery plan can be revoked.");
        string canonicalReason = reason.Trim();
        if (canonicalReason.Length > 1024) throw new ArgumentException("The recovery revocation reason is too long.", nameof(reason));
        RecoveryRevocation revocation = new(atUtc.ToUniversalTime(), canonicalReason,
            plan.Approval?.ApprovedRevision, plan.ContentDigest);
        return Transition(plan, new(plan.ArtifactRevision.Value + 1), RecoveryPlanState.Revoked, atUtc,
            canonicalReason, plan.Validation, plan.Approval, revocation, null);
    }

    public static RecoveryPlan Complete(
        RecoveryPlan plan,
        RecoveryPlanCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(completion);
        if (plan.State != RecoveryPlanState.Approved)
            throw new InvalidOperationException("Only an approved recovery plan can be completed.");
        return Transition(plan, new(plan.ArtifactRevision.Value + 1), RecoveryPlanState.Completed,
            completion.CompletedAtUtc, "Linked recovery execution passed final semantic verification.",
            plan.Validation, plan.Approval, null, completion);
    }

    private static RecoveryPlan Transition(
        RecoveryPlan plan,
        RecoveryPlanArtifactRevision revision,
        RecoveryPlanState state,
        DateTimeOffset atUtc,
        string reason,
        RecoveryPlanValidationResult validation,
        RecoveryApproval? approval,
        RecoveryRevocation? revocation,
        RecoveryPlanCompletion? completion)
    {
        if (!RecoveryPlan.IsLegal(plan.State, state))
            throw new InvalidOperationException($"Illegal recovery-plan transition {plan.State} -> {state}.");
        RecoveryPlanLifecycleEntry[] history = plan.LifecycleHistory.Append(new(
            revision, plan.State, state, atUtc.ToUniversalTime(), reason)).ToArray();
        return new(plan.Id, plan.SchemaVersion, plan.ModelVersion, plan.PolicyVersion, revision, state,
            plan.ContentDigest, plan.CreatedAtUtc, plan.Definition, validation, approval, revocation,
            completion, history);
    }
}
