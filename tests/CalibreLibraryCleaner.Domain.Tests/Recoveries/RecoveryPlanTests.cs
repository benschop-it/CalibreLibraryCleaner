using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Recoveries;

public sealed class RecoveryPlanTests
{
    [Fact]
    public void CanonicalBodyDigestIsStableAndApprovalDoesNotModifyBody()
    {
        RecoveryPlan valid = RecoveryDomainTestData.ValidPlan();
        RecoveryPlan approved = RecoveryPlanLifecyclePolicy.Approve(
            valid, [], DateTimeOffset.UtcNow);

        approved.ContentDigest.Should().Be(valid.ContentDigest);
        RecoveryPlanContentDigestPolicy.Compute(approved.Definition)
            .Should().Be(approved.ContentDigest);
        approved.ArtifactRevision.Value.Should().Be(valid.ArtifactRevision.Value + 1);
        approved.Definition.Should().BeSameAs(valid.Definition);
    }

    [Fact]
    public void RevocationWorksForValidAndApprovedPlansWithoutChangingBody()
    {
        RecoveryPlan valid = RecoveryDomainTestData.ValidPlan();
        RecoveryPlan revokedValid = RecoveryPlanLifecyclePolicy.Revoke(
            valid, "Operator stopped before approval.", DateTimeOffset.UtcNow);
        RecoveryPlan approved = RecoveryPlanLifecyclePolicy.Approve(
            RecoveryDomainTestData.ValidPlan(), [], DateTimeOffset.UtcNow);
        RecoveryPlan revokedApproved = RecoveryPlanLifecyclePolicy.Revoke(
            approved, "Operator revoked approval.", DateTimeOffset.UtcNow);

        revokedValid.State.Should().Be(RecoveryPlanState.Revoked);
        revokedValid.Approval.Should().BeNull();
        revokedApproved.State.Should().Be(RecoveryPlanState.Revoked);
        revokedApproved.Approval.Should().NotBeNull();
        revokedValid.ContentDigest.Should().Be(valid.ContentDigest);
        revokedApproved.ContentDigest.Should().Be(approved.ContentDigest);
    }

    [Fact]
    public void DestructiveGraphRequiresVerifiedNonDestructiveDependency()
    {
        LogicalRecoveryRecordId logical = new("record-test");
        RecoveryOperation destructive = new(new("destroy:test"),
            RecoveryOperationKind.RemoveRecordCreatedByExecution,
            RecoveryOperationPhase.Destructive, logical, new(1), new(1), null,
            null, null, null, null, [],
            new("VERIFY", logical, "record absent", new(1), expectedPresent: false),
            "Remove cleanup-created record.", "journal", RecoveryRiskLevel.Destructive,
            [], "RemoveCleanupCreatedRecord");

        Action action = () => _ = new RecoveryOperationGraph([destructive]);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RecoveredCannotBeReachedWithoutBackupOperationsAndFinalVerification()
    {
        RecoveryPlan approved = RecoveryPlanLifecyclePolicy.Approve(
            RecoveryDomainTestData.ValidPlan(), [], DateTimeOffset.UtcNow);
        RecoveryExecution execution = RecoveryExecution.Create(new(Guid.NewGuid()), approved)
            .Transition(RecoveryExecutionState.PreflightValidating);

        Action action = () => execution.Transition(RecoveryExecutionState.Recovered);

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CanonicalOperationDigestBindsVerificationReasonEvidenceAndSemanticTargets()
    {
        LogicalRecoveryRecordId logical = new("record-canonical");
        RecoveryVerificationExpectation verification = new("VERIFY.EXACT", logical,
            "Expected content remains present.", new(7), "PDF",
            new(10, new(new string('a', 64))));
        RecoveryOperation original = new(new("verify:canonical"),
            RecoveryOperationKind.NoActionAlreadyRestored,
            RecoveryOperationPhase.NonMutating, logical, new(7), new(7), null,
            null, null, null, null, [], verification,
            "Original safety reason.", "journal-entry:42",
            RecoveryRiskLevel.None, [], "VerifyRestoredContent");
        RecoveryOperation altered = new(original.Id, original.Kind, original.Phase,
            original.LogicalRecordId, original.OriginalRecordId,
            original.CurrentRecordId, new(99), original.Format,
            original.OriginalBackupArtifactIdentity,
            original.OriginalBackupFingerprint, original.ExpectedCurrentFingerprint,
            original.DependencyIds,
            new(verification.Code, logical, "Forged verification description.",
                verification.ExpectedCurrentRecordId, verification.Format,
                verification.ExpectedFingerprint, expectedPresent: false),
            "Forged reason.", "forged-journal-evidence", original.Risk,
            original.PreservationDependencyIds, original.RequiredCapability);

        RecoveryOperationGraph.ComputeCanonicalDigest([altered]).Should().NotBe(
            RecoveryOperationGraph.ComputeCanonicalDigest([original]));
    }

    [Fact]
    public void ForgedApprovalPlanIdOrPriorRevisionIsRejected()
    {
        RecoveryPlan approved = RecoveryPlanLifecyclePolicy.Approve(
            RecoveryDomainTestData.ValidPlan(), [], DateTimeOffset.UtcNow);
        RecoveryApproval forged = new(new(Guid.NewGuid()),
            approved.Approval!.ApprovedAtUtc,
            new(approved.Approval.ApprovedRevision.Value + 1),
            approved.Approval.ContentDigest,
            approved.Approval.SourceExecutionId,
            approved.Approval.CurrentLibraryUuid,
            approved.Approval.CanonicalRootIdentityDigest,
            approved.Approval.CurrentStateFingerprint,
            approved.Approval.CapabilityProfile,
            approved.Approval.AcknowledgedWarningCodes);

        Action action = () => _ = new RecoveryPlan(approved.Id,
            approved.SchemaVersion, approved.ModelVersion, approved.PolicyVersion,
            approved.ArtifactRevision, approved.State, approved.ContentDigest,
            approved.CreatedAtUtc, approved.Definition, approved.Validation,
            forged, approved.Revocation, approved.Completion,
            approved.LifecycleHistory);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void RecoveryOperationKindCannotBeRelabeledIntoAnotherExecutionPhase()
    {
        LogicalRecoveryRecordId logical = new("record-phase");

        Action action = () => _ = new RecoveryOperation(new("destroy:forged"),
            RecoveryOperationKind.RemoveFormatAddedByExecution,
            RecoveryOperationPhase.Constructive, logical, new(1), new(1), null,
            "PDF", null, null, new(4, new(new string('a', 64))), [],
            new("VERIFY", logical, "forged phase", new(1), "PDF",
                new(4, new(new string('a', 64))), expectedPresent: false),
            "Forged constructive label.", "journal",
            RecoveryRiskLevel.Constructive, [], "RemoveCleanupAddedFormat");

        action.Should().Throw<ArgumentException>();
    }
}

internal static class RecoveryDomainTestData
{
    public static RecoveryPlan ValidPlan()
    {
        CleanupPlanId sourcePlan = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        CleanupExecutionId sourceExecution =
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        RecoveryPlanId planId = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        CleanupPlanContentDigest sourceDigest = new(new string('a', 64));
        string libraryUuid = "44444444-4444-4444-4444-444444444444";
        Sha256Digest full = new(new string('1', 64));
        Sha256Digest affected = new(new string('2', 64));
        Sha256Digest unrelated = new(new string('3', 64));
        CurrentStateReconciliation reconciliation = CurrentStateReconciliation.Create(
            sourceExecution.ToString(), libraryUuid, 27, full, affected, unrelated,
            [], [], []);
        RecoveryInputIdentity input = new(sourcePlan, CleanupPlanSchemaVersion.V1,
            new(1), sourceDigest, sourceExecution, "cleanup-execution-journal/1.0",
            new(new string('4', 64)), new string('5', 64), null,
            VerifiedBackupManifest.Version, new(new string('6', 64)),
            new(new string('7', 64)), "1.0.0",
            new("C:\\calibredb.exe", "9.11.0", new(new string('8', 64)),
                "calibredb/windows/9.11.0"),
            libraryUuid, 27, new(new string('9', 64)), full, affected, unrelated,
            reconciliation.Version, reconciliation.Digest,
            "calibredb/windows/9.11.0/recovery/1.0");
        RecoveryBackupChain chain = new(sourcePlan, sourceDigest, sourceExecution,
            input.SourceJournalFileDigest, input.SourceJournalFinalEntryHash,
            input.OriginalManifestFileDigest, input.OriginalManifestInternalDigest,
            planId, null);
        RecoveryPlanDefinition definition = new(input,
            new(sourcePlan, sourceExecution, DateTimeOffset.UtcNow,
                CleanupExecutionDisposition.Completed.ToString(), "C:\\backup\\execution"),
            chain, reconciliation, new([]),
            new([], [], [], [], unrelated), []);
        RecoveryPlanValidationResult validation = new([], DateTimeOffset.UtcNow, input);
        return RecoveryPlanLifecyclePolicy.Create(
            planId, definition, validation, DateTimeOffset.UtcNow);
    }
}
