namespace CalibreLibraryCleaner.Domain.Plans;

public enum CleanupPlanApprovalMethod
{
    ExplicitLocalUser,
}

public sealed record CleanupPlanApproval(
    DateTimeOffset ApprovedAtUtc,
    CleanupPlanApprovalMethod Method,
    CleanupPlanArtifactRevision ApprovedRevision,
    CleanupPlanContentDigest ContentDigest);

public sealed record CleanupPlanRevocation(
    DateTimeOffset RevokedAtUtc,
    string Reason,
    CleanupPlanApprovalMethod Method,
    CleanupPlanArtifactRevision PriorApprovalRevision,
    CleanupPlanContentDigest ContentDigest);

public sealed record CleanupPlanLifecycleEntry(
    CleanupPlanArtifactRevision Revision,
    CleanupPlanState FromState,
    CleanupPlanState ToState,
    DateTimeOffset ChangedAtUtc,
    string Reason);

public sealed record CleanupPlan
{
    public CleanupPlan(
        CleanupPlanId id,
        CleanupPlanSchemaVersion schemaVersion,
        CleanupPlanPolicyVersion policyVersion,
        CleanupPlanArtifactRevision artifactRevision,
        CleanupPlanState state,
        CleanupPlanContentDigest contentDigest,
        CleanupPlanInputIdentity inputIdentity,
        DateTimeOffset createdAtUtc,
        DateTimeOffset lastValidatedAtUtc,
        CleanupPlanDefinition definition,
        CleanupPlanValidationResult validation,
        CleanupPlanApproval? approval,
        CleanupPlanRevocation? revocation,
        IEnumerable<CleanupPlanLifecycleEntry> lifecycleHistory)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(schemaVersion);
        ArgumentNullException.ThrowIfNull(policyVersion);
        ArgumentNullException.ThrowIfNull(contentDigest);
        ArgumentNullException.ThrowIfNull(inputIdentity);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(lifecycleHistory);
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        CleanupPlanContentDigest computed = CleanupPlanContentDigestPolicy.Compute(definition);
        if (computed != contentDigest || inputIdentity.DefinitionDigest != contentDigest)
            throw new ArgumentException("Cleanup plan content digest does not match its immutable body.", nameof(contentDigest));
        if (schemaVersion != definition.Provenance.SchemaVersion
            || policyVersion != definition.Provenance.PolicyVersion
            || policyVersion != inputIdentity.PolicyVersion
            || inputIdentity.LibraryUuid != definition.ExpectedLibraryState.LibraryUuid
            || inputIdentity.SchemaVersion != definition.ExpectedLibraryState.SchemaVersion
            || inputIdentity.GroupId != definition.ExpectedLibraryState.GroupId
            || !inputIdentity.MemberIds.SequenceEqual(definition.ExpectedLibraryState.MemberIds)
            || inputIdentity.RecommendationModelVersion != definition.ExpectedLibraryState.RecommendationModelVersion
            || inputIdentity.RecommendationInputVersion != definition.ExpectedLibraryState.RecommendationInputVersion
            || inputIdentity.GroupId != definition.Provenance.GroupId
            || inputIdentity.RecommendationModelVersion != definition.Provenance.RecommendationModelVersion
            || inputIdentity.RecommendationInputVersion != definition.Provenance.RecommendationInputVersion)
            throw new ArgumentException("Cleanup plan identity, policy, provenance, and expected state disagree.", nameof(inputIdentity));
        CleanupPlanLifecycleEntry[] history = lifecycleHistory.OrderBy(value => value.Revision.Value)
            .Select(value => value with { ChangedAtUtc = value.ChangedAtUtc.ToUniversalTime(), Reason = value.Reason?.Trim() ?? string.Empty })
            .ToArray();
        ValidateLifecycle(state, artifactRevision, contentDigest, validation, approval, revocation, history);
        Id = id;
        SchemaVersion = schemaVersion;
        PolicyVersion = policyVersion;
        ArtifactRevision = artifactRevision;
        State = state;
        ContentDigest = contentDigest;
        InputIdentity = inputIdentity;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        LastValidatedAtUtc = lastValidatedAtUtc.ToUniversalTime();
        Definition = definition;
        Validation = validation;
        Approval = approval;
        Revocation = revocation;
        LifecycleHistory = Array.AsReadOnly(history);
    }

    public CleanupPlanId Id { get; }
    public CleanupPlanSchemaVersion SchemaVersion { get; }
    public CleanupPlanPolicyVersion PolicyVersion { get; }
    public CleanupPlanArtifactRevision ArtifactRevision { get; }
    public CleanupPlanState State { get; }
    public CleanupPlanContentDigest ContentDigest { get; }
    public CleanupPlanInputIdentity InputIdentity { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset LastValidatedAtUtc { get; }
    public CleanupPlanDefinition Definition { get; }
    public CleanupPlanValidationResult Validation { get; }
    public CleanupPlanApproval? Approval { get; }
    public CleanupPlanRevocation? Revocation { get; }
    public IReadOnlyList<CleanupPlanLifecycleEntry> LifecycleHistory { get; }

    private static void ValidateLifecycle(
        CleanupPlanState state,
        CleanupPlanArtifactRevision revision,
        CleanupPlanContentDigest digest,
        CleanupPlanValidationResult validation,
        CleanupPlanApproval? approval,
        CleanupPlanRevocation? revocation,
        CleanupPlanLifecycleEntry[] history)
    {
        if (history.Length == 0 || history[^1].Revision != revision || history[^1].ToState != state
            || history[0].Revision.Value != 1 || history[0].FromState != CleanupPlanState.Draft
            || history.Select(value => value.Revision.Value).Distinct().Count() != history.Length
            || history.Where((value, index) => value.Revision.Value != index + 1).Any()
            || history.Where((value, index) => index > 0 && value.FromState != history[index - 1].ToState).Any()
            || history.Any(value => !Enum.IsDefined(value.FromState) || !Enum.IsDefined(value.ToState)
                || !IsLegal(value.FromState, value.ToState) || string.IsNullOrWhiteSpace(value.Reason) || value.Reason.Length > 1024))
            throw new ArgumentException("Cleanup plan lifecycle history is invalid.", nameof(history));
        if (state is CleanupPlanState.Valid or CleanupPlanState.Approved && !validation.IsValid)
            throw new ArgumentException("Valid and approved plans cannot contain blocking issues.", nameof(validation));
        if (approval is not null && (!Enum.IsDefined(approval.Method) || approval.ContentDigest != digest))
            throw new ArgumentException("Approval is not bound to the cleanup-plan body.", nameof(approval));
        CleanupPlanLifecycleEntry? approvalEntry = history.SingleOrDefault(value => value.ToState == CleanupPlanState.Approved);
        CleanupPlanLifecycleEntry? revocationEntry = history.SingleOrDefault(value => value.ToState == CleanupPlanState.Revoked);
        bool historyWasApproved = approvalEntry is not null;
        if (historyWasApproved != (approval is not null)
            || approval is not null && (approvalEntry!.ChangedAtUtc != approval.ApprovedAtUtc.ToUniversalTime()
                || approval.ApprovedRevision.Value != approvalEntry.Revision.Value - 1))
            throw new ArgumentException("Approval does not match the lifecycle history.", nameof(approval));
        if ((state is CleanupPlanState.Draft or CleanupPlanState.Valid or CleanupPlanState.Blocked) && (approval is not null || revocation is not null)
            || state == CleanupPlanState.Approved && (approval is null || revocation is not null)
            || state == CleanupPlanState.Revoked && (approval is null || revocation is null)
            || state != CleanupPlanState.Revoked && revocation is not null)
            throw new ArgumentException("The lifecycle state has inconsistent approval audit information.");
        if (revocation is not null && (approval is null || !Enum.IsDefined(revocation.Method)
            || revocation.ContentDigest != digest || revocation.PriorApprovalRevision != approval.ApprovedRevision
            || revocationEntry is null || revocationEntry.ChangedAtUtc != revocation.RevokedAtUtc.ToUniversalTime()
            || string.IsNullOrWhiteSpace(revocation.Reason) || revocation.Reason.Length > 1024
            || !string.Equals(revocation.Reason, revocation.Reason.Trim(), StringComparison.Ordinal)
            || !string.Equals(revocationEntry.Reason, revocation.Reason, StringComparison.Ordinal)))
            throw new ArgumentException("Revocation is not bound to its approval.", nameof(revocation));
        if ((state == CleanupPlanState.Revoked) != (revocationEntry is not null))
            throw new ArgumentException("Revocation does not match the lifecycle history.", nameof(revocation));
    }

    public static bool IsLegal(CleanupPlanState from, CleanupPlanState to) => (from, to) switch
    {
        (CleanupPlanState.Draft, CleanupPlanState.Valid or CleanupPlanState.Blocked) => true,
        (CleanupPlanState.Valid, CleanupPlanState.Approved or CleanupPlanState.Stale or CleanupPlanState.Blocked) => true,
        (CleanupPlanState.Approved, CleanupPlanState.Stale or CleanupPlanState.Revoked) => true,
        _ => false,
    };
}

public static class CleanupPlanLifecyclePolicy
{
    public static CleanupPlan Approve(
        CleanupPlan plan,
        CleanupPlanValidationResult validation,
        DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(validation);
        if (plan.State != CleanupPlanState.Valid || !validation.IsValid
            || validation.ValidatedInputIdentity != plan.InputIdentity)
            throw new InvalidOperationException("Only a current valid plan can be approved.");
        CleanupPlanArtifactRevision revision = new(plan.ArtifactRevision.Value + 1);
        CleanupPlanApproval approval = new(atUtc.ToUniversalTime(), CleanupPlanApprovalMethod.ExplicitLocalUser, plan.ArtifactRevision, plan.ContentDigest);
        return Transition(plan, revision, CleanupPlanState.Approved, atUtc, "Explicit approval of the canonical immutable body.", validation, approval, null);
    }

    public static CleanupPlan Revoke(CleanupPlan plan, string reason, DateTimeOffset atUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (reason.Trim().Length > 1024) throw new ArgumentException("The revocation reason exceeds its bound.", nameof(reason));
        if (plan.State != CleanupPlanState.Approved || plan.Approval is null)
            throw new InvalidOperationException("Only an approved plan can be revoked.");
        CleanupPlanArtifactRevision revision = new(plan.ArtifactRevision.Value + 1);
        CleanupPlanRevocation revocation = new(atUtc.ToUniversalTime(), reason.Trim(), CleanupPlanApprovalMethod.ExplicitLocalUser, plan.Approval.ApprovedRevision, plan.ContentDigest);
        return Transition(plan, revision, CleanupPlanState.Revoked, atUtc, reason.Trim(), plan.Validation, plan.Approval, revocation);
    }

    public static CleanupPlan MarkStale(CleanupPlan plan, CleanupPlanValidationResult validation, DateTimeOffset atUtc)
    {
        if (plan.State is not (CleanupPlanState.Valid or CleanupPlanState.Approved))
            return plan;
        CleanupPlanArtifactRevision revision = new(plan.ArtifactRevision.Value + 1);
        return Transition(plan, revision, CleanupPlanState.Stale, atUtc, "Relevant source-library or recommendation input changed.", validation, plan.Approval, plan.Revocation);
    }

    private static CleanupPlan Transition(
        CleanupPlan plan,
        CleanupPlanArtifactRevision revision,
        CleanupPlanState state,
        DateTimeOffset atUtc,
        string reason,
        CleanupPlanValidationResult validation,
        CleanupPlanApproval? approval,
        CleanupPlanRevocation? revocation)
    {
        if (!CleanupPlan.IsLegal(plan.State, state)) throw new InvalidOperationException($"Illegal cleanup plan transition {plan.State} -> {state}.");
        CleanupPlanLifecycleEntry[] history = plan.LifecycleHistory.Append(new(revision, plan.State, state, atUtc.ToUniversalTime(), reason)).ToArray();
        return new(plan.Id, plan.SchemaVersion, plan.PolicyVersion, revision, state, plan.ContentDigest, plan.InputIdentity,
            plan.CreatedAtUtc, atUtc, plan.Definition, validation, approval, revocation, history);
    }
}
