using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Executions;

public sealed class CleanupExecutionDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ApprovedV1PlanMapsToConstructiveOperationBeforeRecordRemoval()
    {
        CleanupPlan plan = ApprovedPlan();

        CleanupExecutionCapabilityResult result = CleanupExecutionCapabilityPolicy.Evaluate(plan);

        result.IsSupported.Should().BeTrue();
        result.Graph!.Operations.Select(value => value.Kind).Should().ContainInOrder(
            ExecutionOperationKind.VerifyMetadataPreserved,
            ExecutionOperationKind.AddOrReplaceFormat,
            ExecutionOperationKind.RemoveRedundantRecord);
        CleanupExecutionOperation removal = result.Graph.DestructiveOperations.Single();
        removal.DependencyIds.Should().Contain(result.Graph.ConstructiveOperations.Single().Id);
    }

    [Fact]
    public void LifecycleCannotCrossMutationBoundaryBeforeVerifiedBackup()
    {
        (CleanupExecution execution, _) = CreatedExecution();
        execution = execution.Transition(CleanupExecutionState.AcquiringLease)
            .Transition(CleanupExecutionState.PreflightValidating)
            .Transition(CleanupExecutionState.ReadyForBackup)
            .Transition(CleanupExecutionState.BackingUp);

        Action start = () => execution.MarkMutationStarting();
        Action ready = () => execution.Transition(CleanupExecutionState.ReadyToExecute);

        start.Should().Throw<InvalidOperationException>();
        ready.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SerialVerifiedOperationsCanCompleteOnlyInDependencyOrder()
    {
        (CleanupExecution execution, VerifiedBackupManifest manifest) = CreatedExecution();
        execution = execution.Transition(CleanupExecutionState.AcquiringLease)
            .Transition(CleanupExecutionState.PreflightValidating)
            .Transition(CleanupExecutionState.ReadyForBackup)
            .Transition(CleanupExecutionState.BackingUp)
            .AttachVerifiedBackup(manifest)
            .Transition(CleanupExecutionState.ReadyToExecute);
        CleanupExecutionOperation metadata = execution.Graph.Operations[0];
        CleanupExecutionOperation add = execution.Graph.ConstructiveOperations.Single();
        CleanupExecutionOperation remove = execution.Graph.DestructiveOperations.Single();

        Action removeEarly = () => execution.StartOperation(remove.Id);
        removeEarly.Should().Throw<InvalidOperationException>();

        execution = execution.SatisfyNoOp(metadata.Id).MarkMutationStarting();
        execution = execution.StartOperation(add.Id).MarkOperationSucceeded(add.Id).MarkOperationVerified(add.Id);
        execution = execution.StartOperation(remove.Id).MarkOperationSucceeded(remove.Id).MarkOperationVerified(remove.Id);
        execution = execution.Transition(CleanupExecutionState.Verifying).Transition(CleanupExecutionState.Completed);

        execution.State.Should().Be(CleanupExecutionState.Completed);
        execution.Disposition.Should().Be(CleanupExecutionDisposition.Completed);
        execution.Operations.Should().OnlyContain(value => value.Status == ExecutionOperationStatus.Verified
            || value.Status == ExecutionOperationStatus.SatisfiedNoOp);
    }

    [Fact]
    public void PostMutationFailureRequiresExplicitRecoveryDisposition()
    {
        (CleanupExecution execution, VerifiedBackupManifest manifest) = CreatedExecution();
        execution = execution.Transition(CleanupExecutionState.AcquiringLease)
            .Transition(CleanupExecutionState.PreflightValidating)
            .Transition(CleanupExecutionState.ReadyForBackup)
            .Transition(CleanupExecutionState.BackingUp)
            .AttachVerifiedBackup(manifest)
            .Transition(CleanupExecutionState.ReadyToExecute);
        execution = execution.SatisfyNoOp(execution.Graph.Operations[0].Id).MarkMutationStarting();
        CleanupExecutionOperation add = execution.Graph.ConstructiveOperations.Single();

        execution = execution.StartOperation(add.Id)
            .MarkOperationFailed(add.Id, "CONTROLLED", CleanupExecutionFailureClassification.ConstructiveCommand)
            .RequireRecovery(CleanupExecutionFailureClassification.ConstructiveCommand);

        execution.State.Should().Be(CleanupExecutionState.RecoveryRequired);
        execution.Disposition.Should().Be(CleanupExecutionDisposition.RecoveryRequired);
        execution.MutationStarted.Should().BeTrue();
    }

    [Fact]
    public void ManifestCoverageRejectsAnyMissingMandatoryPlanArtifact()
    {
        CleanupPlan plan = ApprovedPlan();
        BackupManifestEntry onlyPlan = new("plan.json", BackupArtifactKind.CleanupPlan, 1,
            new(new string('a', 64)), ["plan-artifact"]);
        VerifiedBackupManifest manifest = VerifiedBackupManifest.Create(new(Guid.NewGuid()), plan, Now, [onlyPlan]);

        IReadOnlyList<ExecutionIssue> issues = BackupManifestCoveragePolicy.Validate(plan, manifest);

        issues.Should().Contain(value => value.Code == "EXECUTION.BACKUP_REQUIREMENT_MISSING");
    }

    [Fact]
    public void SemanticVerificationRequiresSelectedHashAndRemovedSource()
    {
        CleanupPlan plan = ApprovedPlan();
        LibrarySnapshot constructive = Snapshot(targetHasFormat: true, sourceExists: true, changedHash: false);
        Sha256Digest baseline = ExecutionSnapshotDigestPolicy.ComputeUnaffected(constructive, plan.Definition.InvolvedRecordIds);

        ExecutionVerificationResult intermediate = CleanupExecutionVerificationPolicy.VerifyConstructiveState(
            plan, constructive, ["retain:PDF"], [], baseline, Now);
        ExecutionVerificationResult final = CleanupExecutionVerificationPolicy.VerifyFinalState(
            plan, Snapshot(targetHasFormat: true, sourceExists: false, changedHash: false), baseline, Now);
        ExecutionVerificationResult changed = CleanupExecutionVerificationPolicy.VerifyConstructiveState(
            plan, Snapshot(targetHasFormat: true, sourceExists: true, changedHash: true), ["retain:PDF"], [], baseline, Now);

        intermediate.IsVerified.Should().BeTrue();
        final.IsVerified.Should().BeTrue();
        changed.IsVerified.Should().BeFalse();
        changed.Issues.Should().Contain(value => value.Code == "EXECUTION.RETAINED_FORMAT_MISMATCH");
    }

    [Fact]
    public void NonApprovedPlanHasNoExecutableCapabilityGraph()
    {
        CleanupPlan approved = ApprovedPlan();
        CleanupPlan valid = new(approved.Id, approved.SchemaVersion, approved.PolicyVersion, new(1),
            CleanupPlanState.Valid, approved.ContentDigest, approved.InputIdentity, approved.CreatedAtUtc,
            approved.LastValidatedAtUtc, approved.Definition, approved.Validation, null, null,
            approved.LifecycleHistory.Take(1));

        CleanupExecutionCapabilityResult result = CleanupExecutionCapabilityPolicy.Evaluate(valid);

        result.IsSupported.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "EXECUTION.APPROVAL_INVALID");
    }

    [Fact]
    public void LifecycleTransitionTableIsClosedToTheDocumentedEdges()
    {
        HashSet<(CleanupExecutionState From, CleanupExecutionState To)> allowed =
        [
            (CleanupExecutionState.Created, CleanupExecutionState.AcquiringLease),
            (CleanupExecutionState.AcquiringLease, CleanupExecutionState.PreflightValidating),
            (CleanupExecutionState.AcquiringLease, CleanupExecutionState.PreflightFailed),
            (CleanupExecutionState.AcquiringLease, CleanupExecutionState.CancelledBeforeMutation),
            (CleanupExecutionState.PreflightValidating, CleanupExecutionState.ReadyForBackup),
            (CleanupExecutionState.PreflightValidating, CleanupExecutionState.PreflightFailed),
            (CleanupExecutionState.PreflightValidating, CleanupExecutionState.CancelledBeforeMutation),
            (CleanupExecutionState.ReadyForBackup, CleanupExecutionState.BackingUp),
            (CleanupExecutionState.ReadyForBackup, CleanupExecutionState.BackupFailed),
            (CleanupExecutionState.ReadyForBackup, CleanupExecutionState.CancelledBeforeMutation),
            (CleanupExecutionState.BackingUp, CleanupExecutionState.BackupVerified),
            (CleanupExecutionState.BackingUp, CleanupExecutionState.BackupFailed),
            (CleanupExecutionState.BackingUp, CleanupExecutionState.CancelledBeforeMutation),
            (CleanupExecutionState.BackupVerified, CleanupExecutionState.ReadyToExecute),
            (CleanupExecutionState.BackupVerified, CleanupExecutionState.ExecutionFailedBeforeMutation),
            (CleanupExecutionState.BackupVerified, CleanupExecutionState.CancelledBeforeMutation),
            (CleanupExecutionState.ReadyToExecute, CleanupExecutionState.Executing),
            (CleanupExecutionState.ReadyToExecute, CleanupExecutionState.Verifying),
            (CleanupExecutionState.ReadyToExecute, CleanupExecutionState.ExecutionFailedBeforeMutation),
            (CleanupExecutionState.ReadyToExecute, CleanupExecutionState.CancelledBeforeMutation),
            (CleanupExecutionState.Executing, CleanupExecutionState.Verifying),
            (CleanupExecutionState.Executing, CleanupExecutionState.ExecutionPartiallyApplied),
            (CleanupExecutionState.Executing, CleanupExecutionState.VerificationFailed),
            (CleanupExecutionState.Executing, CleanupExecutionState.RecoveryRequired),
            (CleanupExecutionState.ExecutionPartiallyApplied, CleanupExecutionState.RecoveryRequired),
            (CleanupExecutionState.VerificationFailed, CleanupExecutionState.RecoveryRequired),
            (CleanupExecutionState.Verifying, CleanupExecutionState.Completed),
            (CleanupExecutionState.Verifying, CleanupExecutionState.VerificationFailed),
            (CleanupExecutionState.Verifying, CleanupExecutionState.RecoveryRequired),
        ];

        foreach (CleanupExecutionState from in Enum.GetValues<CleanupExecutionState>())
            foreach (CleanupExecutionState to in Enum.GetValues<CleanupExecutionState>())
                CleanupExecution.IsLegal(from, to).Should().Be(allowed.Contains((from, to)), $"the edge {from} -> {to} is explicitly governed");
    }

    [Fact]
    public void CapabilityGraphHasStableIdsAndOrderForTheSameImmutablePlan()
    {
        CleanupPlan plan = ApprovedPlan();

        CleanupExecutionOperationGraph firstGraph = CleanupExecutionCapabilityPolicy.Evaluate(plan).Graph!;
        string[] first = firstGraph.Operations
            .Select(value => value.Id.Value).ToArray();
        CleanupExecutionOperationGraph secondGraph = CleanupExecutionCapabilityPolicy.Evaluate(plan).Graph!;
        string[] second = secondGraph.Operations
            .Select(value => value.Id.Value).ToArray();

        first.Should().Equal(second).And.Equal("verify:metadata", "construct:format:PDF", "destroy:record:2");
        firstGraph.Digest.Should().Be(secondGraph.Digest);
    }

    [Fact]
    public void ConfirmationRejectsLibraryRootOrOperationGraphSubstitution()
    {
        (CleanupExecution execution, _) = CreatedExecution();
        CleanupPlan plan = ApprovedPlan();

        execution.Confirmation.Matches(plan, execution.Confirmation.ToolIdentity, "external-backup",
            "C:\\synthetic\\library", execution.Graph.Digest).Should().BeTrue();
        execution.Confirmation.Matches(plan, execution.Confirmation.ToolIdentity, "external-backup",
            "D:\\copied-library", execution.Graph.Digest).Should().BeFalse();
        execution.Confirmation.Matches(plan, execution.Confirmation.ToolIdentity, "external-backup",
            "C:\\synthetic\\library", new(new string('f', 64))).Should().BeFalse();
    }

    [Fact]
    public void CoverBearingPlanFailsClosedUntilByteExactCoverVerificationExists()
    {
        CleanupExecutionCapabilityResult result = CleanupExecutionCapabilityPolicy.Evaluate(
            ApprovedPlan(targetHasCover: true));

        result.IsSupported.Should().BeFalse();
        result.Issues.Should().Contain(value =>
            value.Code == "EXECUTION.COVER_CONTENT_VERIFICATION_UNSUPPORTED");
    }

    [Fact]
    public void CompletionIsRejectedWhileAnyOperationIsUnverified()
    {
        (CleanupExecution execution, VerifiedBackupManifest manifest) = CreatedExecution();
        execution = execution.Transition(CleanupExecutionState.AcquiringLease)
            .Transition(CleanupExecutionState.PreflightValidating)
            .Transition(CleanupExecutionState.ReadyForBackup)
            .Transition(CleanupExecutionState.BackingUp)
            .AttachVerifiedBackup(manifest)
            .Transition(CleanupExecutionState.ReadyToExecute);
        execution = execution.SatisfyNoOp(execution.Graph.Operations[0].Id).MarkMutationStarting()
            .Transition(CleanupExecutionState.Verifying);

        Action complete = () => execution.Transition(CleanupExecutionState.Completed);

        complete.Should().Throw<InvalidOperationException>();
    }

    private static (CleanupExecution Execution, VerifiedBackupManifest Manifest) CreatedExecution()
    {
        CleanupPlan plan = ApprovedPlan();
        CleanupExecutionCapabilityResult capability = CleanupExecutionCapabilityPolicy.Evaluate(plan);
        CleanupExecutionId id = new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        ExecutionToolIdentity tool = new("trusted-calibredb", "9.11.0", new(new string('b', 64)), "calibredb/windows/9.11.0");
        CleanupExecutionConfirmation confirmation = new(plan.Id, plan.ArtifactRevision, plan.ContentDigest,
            plan.InputIdentity.LibraryUuid, "C:\\synthetic\\library", capability.Graph!.Digest,
            tool, "external-backup", Now, true, true);
        CleanupExecution execution = CleanupExecution.Create(id, plan, confirmation, capability.Graph!);
        BackupManifestEntry[] entries = plan.Definition.BackupRequirements
            .Where(value => value.Kind != BackupRequirementKind.ExecutionAudit)
            .Select((value, index) => new BackupManifestEntry($"artifact/{index}", BackupArtifactKind.PreflightEvidence,
                1, new(new string((char)('a' + index), 64)), [value.Id], value.RecordId, value.Format)).ToArray();
        return (execution, VerifiedBackupManifest.Create(id, plan, Now, entries));
    }

    private static CleanupPlan ApprovedPlan(bool targetHasCover = false)
    {
        CalibreBookId target = new(1);
        CalibreBookId source = new(2);
        ExactMetadataDuplicateGroupId group = new("exact-metadata:test");
        RecommendationInputVersion input = new("input/1");
        ExpectedFormatState sourceFormat = new(source, "PDF", "book", "Author/Shared (2)/book.pdf",
            FormatFileStatus.Present, Fingerprint(false), Observation());
        ExpectedRecordState targetRecord = Record(target, [], targetHasCover);
        ExpectedRecordState sourceRecord = Record(source, [sourceFormat]);
        ExpectedLibraryState expected = new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, group,
            [target, source], [targetRecord, sourceRecord], RecommendationModelVersion.V1, input);
        FormatRetentionInstruction retention = new("retain:PDF", "PDF", target, sourceFormat,
            FormatRetentionMode.RetainFromOtherRecord, "review:PDF:2");
        List<BackupRequirement> backups =
        [
            new("metadata:1", BackupRequirementKind.RecordMetadataSnapshot, target, null, true, "metadata"),
            new("metadata:2", BackupRequirementKind.RecordMetadataSnapshot, source, null, true, "metadata"),
            new("format:2:PDF", BackupRequirementKind.FormatFile, source, "PDF", true, "format"),
            new("state:2:PDF", BackupRequirementKind.ManagedPathAndFileState, source, "PDF", true, "state"),
            new("plan-artifact", BackupRequirementKind.CleanupPlanArtifact, null, null, true, "plan"),
            new("execution-audit", BackupRequirementKind.ExecutionAudit, null, null, true, "audit"),
        ];
        if (targetHasCover)
            backups.Add(new("cover:1", BackupRequirementKind.CoverIfPresent, target, null, true, "cover"));
        CleanupPlanFormatSelectionProvenance selection = new("PDF", FormatResolutionStatus.Selected, source,
            [source], [], []);
        CleanupPlanProvenance provenance = new(group, "shared", ["author"], "EXACT_NORMALIZED_TITLE_AUTHOR_SET",
            RecommendationModelVersion.V1, input, RecommendationConfidence.Deterministic, target,
            [selection], [], [], RecommendationReviewStatus.Accepted, RecommendationFreshness.Current,
            target, [selection], new(RecommendationReviewStatus.Accepted, Now, null, [], []),
            CleanupPlanSchemaVersion.V1, CleanupPlanPolicyVersion.V1, Now);
        CleanupPlanDefinition definition = new(expected, target, [target, source], new(target, target, target),
            [retention], [new(source, "PDF", sourceFormat.RelativePath, sourceFormat,
                FormatRemovalReason.RemovedWithSourceRecordAfterRetention, retention.Id, "format:2:PDF", false)],
            [new(source, ["metadata:2", "format:2:PDF", "state:2:PDF"], [retention.Id])], backups, provenance);
        CleanupPlanContentDigest digest = CleanupPlanContentDigestPolicy.Compute(definition);
        CleanupPlanInputIdentity identity = new(expected.LibraryUuid, expected.SchemaVersion, group, [target, source],
            RecommendationModelVersion.V1, input, CleanupPlanPolicyVersion.V1, digest);
        CleanupPlanValidationResult validation = new(CleanupPlanRequiredIssuePolicy.Create(definition), Now, identity);
        CleanupPlan valid = new(new(Guid.Parse("11111111-2222-3333-4444-555555555555")), CleanupPlanSchemaVersion.V1,
            CleanupPlanPolicyVersion.V1, new(1), CleanupPlanState.Valid, digest, identity, Now, Now,
            definition, validation, null, null, [new(new(1), CleanupPlanState.Draft, CleanupPlanState.Valid, Now, "Validated.")]);
        return CleanupPlanLifecyclePolicy.Approve(valid, validation, Now.AddMinutes(1));
    }

    private static LibrarySnapshot Snapshot(bool targetHasFormat, bool sourceExists, bool changedHash)
    {
        List<CalibreBook> books =
        [
            Book(new(1), targetHasFormat
                ? [new("PDF", "book", "Author/Shared (1)/book.pdf", FormatFileStatus.Present,
                    Fingerprint(changedHash), new(5, Now, Now, 32))]
                : []),
        ];
        if (sourceExists)
            books.Add(Book(new(2), [new("PDF", "book", "Author/Shared (2)/book.pdf", FormatFileStatus.Present,
                Fingerprint(false), Observation())]));
        return new(new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\synthetic\\library"), Now, books, []);
    }

    private static ExpectedRecordState Record(
        CalibreBookId id,
        IEnumerable<ExpectedFormatState> formats,
        bool hasCover = false) => new(
        id, "Shared", "Author", [new(new(id.Value), "Author", "Author")], [new("isbn", "9780306406157")],
        null, null, null, null, ["eng"], hasCover, $"Author/Shared ({id.Value})", formats);

    private static CalibreBook Book(CalibreBookId id, IEnumerable<BookFormat> formats) => new(
        id, "Shared", "Author", [new(new(id.Value), "Author", "Author")], [new("isbn", "9780306406157")],
        formats, $"Author/Shared ({id.Value})", new(languages: ["eng"]));

    private static FormatFileFingerprint Fingerprint(bool changed) => new(5, new(new string(changed ? 'f' : 'a', 64)));
    private static FormatFileObservation Observation() => new(5, Now.AddDays(-1), Now.AddHours(-1), 32);
}
