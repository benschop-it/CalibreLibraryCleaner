using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recoveries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Recoveries;

public sealed class GenerateRecoveryPlanUseCaseTests
{
    [Fact]
    public void CompletedCleanupProducesConstructiveRestorationBeforeDestruction()
    {
        RecoveryPlan plan = RecoveryTestData.ValidPlan();

        plan.State.Should().Be(RecoveryPlanState.Valid);
        plan.Definition.OperationGraph.ConstructiveOperations.Should().NotBeEmpty();
        plan.Definition.OperationGraph.DestructiveOperations.Should().NotBeEmpty();
        int lastConstructive = plan.Definition.OperationGraph.Operations.ToList().FindLastIndex(
            value => value.Phase == RecoveryOperationPhase.Constructive);
        int firstDestructive = plan.Definition.OperationGraph.Operations.ToList().FindIndex(
            value => value.Phase == RecoveryOperationPhase.Destructive);
        firstDestructive.Should().BeGreaterThan(lastConstructive);
        plan.Definition.OperationGraph.DestructiveOperations.Should().OnlyContain(value =>
            value.DependencyIds.Any(id =>
                plan.Definition.OperationGraph.Operations.Single(operation => operation.Id == id)
                    .Phase != RecoveryOperationPhase.Destructive));
    }

    [Fact]
    public void ApprovalBindsExactDigestInputAndEveryWarning()
    {
        RecoveryPlan valid = RecoveryTestData.ValidPlan();

        Action missingWarning = () => RecoveryPlanLifecyclePolicy.Approve(
            valid, [], DateTimeOffset.UtcNow);
        RecoveryPlan approved = RecoveryPlanLifecyclePolicy.Approve(valid,
            valid.Definition.RequiredWarningCodes, DateTimeOffset.UtcNow);

        if (valid.Definition.RequiredWarningCodes.Count > 0)
            missingWarning.Should().Throw<InvalidOperationException>();
        approved.State.Should().Be(RecoveryPlanState.Approved);
        approved.ContentDigest.Should().Be(valid.ContentDigest);
        approved.Approval!.CurrentStateFingerprint.Should().Be(
            valid.Definition.InputIdentity.FullStateFingerprint);
        approved.Approval.AcknowledgedWarningCodes.Should().Equal(
            valid.Definition.RequiredWarningCodes);
    }

    [Fact]
    public void SemanticInputChangeRequiresNewPlanIdentityAndDigest()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current,
            RecoveryCapabilityProfile profile, _) = RecoveryTestData.SourceAndCurrent();
        RecoveryPlan first = RecoveryTestData.PlanFor(source, current, profile,
            Guid.Parse("10000000-0000-0000-0000-000000000001"));
        RecoveryPlan second = RecoveryTestData.PlanFor(source,
            current with { FullFingerprint = new(new string('8', 64)) },
            profile, Guid.Parse("10000000-0000-0000-0000-000000000002"));

        first.Id.Should().NotBe(second.Id);
        first.ContentDigest.Should().NotBe(second.ContentDigest);
    }

    [Fact]
    public void UnexpectedCurrentContentBecomesExplicitPreservationExpectation()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current,
            RecoveryCapabilityProfile profile, _) = RecoveryTestData.SourceAndCurrent();
        // The completed cleanup fixture already has a cleanup-added target PDF that differs
        // from the target's verified pre-state and must not disappear without dependencies.
        RecoveryPlan plan = RecoveryTestData.PlanFor(source, current, profile);

        plan.Definition.OperationGraph.DestructiveOperations.Should().OnlyContain(value =>
            value.Risk == RecoveryRiskLevel.Destructive);
        plan.Definition.OperationGraph.Operations.Should().NotContain(value =>
            value.Kind == RecoveryOperationKind.RemoveFormatAddedByExecution
            && value.DependencyIds.Count == 0);
    }

    [Fact]
    public void IndependentlyModifiedRecordIsPreservedAndPreStateUsesSeparateRecoveredRecord()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current,
            RecoveryCapabilityProfile profile, _) = RecoveryTestData.SourceAndCurrent();
        CalibreBook target = current.Snapshot.Books.Single();
        CalibreBook modified = new(target.Id, "User changed title", target.AuthorSort,
            target.Authors, target.Identifiers, target.Formats,
            target.RelativeDirectory, target.PublicationMetadata);
        RecoveryCurrentStateSnapshot changed = RecoveryTestData.CurrentFrom(source,
            new(current.Snapshot.Identity, current.Snapshot.ScannedAt.AddMinutes(1),
                [modified], []));

        RecoveryPlan plan = RecoveryTestData.PlanFor(source, changed, profile);

        LogicalRecoveryRecordId targetLogical = LogicalRecoveryRecordId.Create(
            source.Journal!.ExecutionId, target.Id);
        plan.Definition.OperationGraph.Operations.Should().Contain(value =>
            value.LogicalRecordId == targetLogical
            && value.Kind == RecoveryOperationKind.CreateRecoveredRecord,
            string.Join(" | ", plan.Definition.Issues.Select(value =>
                $"{value.Code}: {value.Explanation}")));
        plan.Definition.ExpectedFinalState.PreservedContent.Should().Contain(value =>
            value.CurrentRecordId == target.Id && value.Format == "PDF");
        plan.Definition.OperationGraph.Operations.Should().NotContain(value =>
            value.LogicalRecordId == targetLogical
            && value.Kind == RecoveryOperationKind.RemoveFormatAddedByExecution);
    }

    [Fact]
    public void CanonicalPlanDigestBindsExpectedMetadataFieldsAndPreservationSemantics()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current,
            RecoveryCapabilityProfile profile, _) = RecoveryTestData.SourceAndCurrent();
        RecoveryPlan plan = RecoveryTestData.PlanFor(source, current, profile);
        ExpectedRecoveredState expected = plan.Definition.ExpectedFinalState;
        ExpectedRecoveredRecordState first = expected.Records[0];
        ExpectedRecoveredRecordState alteredRecord = new(first.LogicalRecordId,
            first.OriginalState, first.ExpectedCurrentRecordId,
            first.SupportedMetadataFields.Append("forged-field").ToArray());
        ExpectedRecoveredState alteredExpected = new(
            expected.Records.Skip(1).Prepend(alteredRecord),
            expected.PreservedContent.Select(value => new PreservedContentExpectation(
                value.LogicalRecordId, value.CurrentRecordId, value.Format,
                value.Fingerprint, value.PreservationReason + " forged")),
            expected.ExpectedAbsentFormats, expected.ExpectedAbsentRecords,
            expected.UnrelatedStateFingerprint);
        RecoveryPlanDefinition altered = new(plan.Definition.InputIdentity,
            plan.Definition.Provenance, plan.Definition.OriginalBackupChain,
            plan.Definition.Reconciliation, plan.Definition.OperationGraph,
            alteredExpected, plan.Definition.Issues);

        RecoveryPlanContentDigestPolicy.Compute(altered)
            .Should().NotBe(plan.ContentDigest);
    }

    [Fact]
    public void RecreatedRecordRequiresEveryModeledMetadataCapability()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current,
            RecoveryCapabilityProfile profile, _) = RecoveryTestData.SourceAndCurrent();
        RecoveryCapabilityProfile incomplete = new(profile.ProfileIdentity,
            profile.ToolIdentity, profile.Capabilities.Select(value =>
                value.Capability == RecoveryCapability.RestoreIdentifiers
                    ? new RecoveryCapabilityStatus(value.Capability,
                        value.Documented, value.ClosedMappingTested,
                        value.RealCalibreQualified, false,
                        "identifiers deliberately unqualified")
                    : value));

        RecoveryPlan plan = RecoveryTestData.PlanFor(source, current, incomplete);

        plan.Definition.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.COMPLETE_METADATA_RESTORE_UNSUPPORTED"
            && value.Severity == RecoveryIssueSeverity.Blocking);
        plan.Definition.OperationGraph.Operations.Should().OnlyContain(value =>
            value.Kind == RecoveryOperationKind.ManualInterventionRequired);
    }

    [Fact]
    public void ByteIdenticalBackupSubstitutionAtAnotherBundleIdentityIsRejected()
    {
        (RecoverySourceInspection source, _, _, _) =
            RecoveryTestData.SourceAndCurrent();
        RecoveryPlan plan = RecoveryTestData.ValidPlan();
        RecoverySourceInspection substituted = source with
        {
            BundleIdentity = "C:\\backup\\substituted-execution",
        };

        PrepareRecoveryExecutionUseCase.SourceMatchesPlan(
            substituted, plan).Should().BeFalse();
    }
}
