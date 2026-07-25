using System.Reflection;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class RecoveryWorkspaceViewModelTests
{
    [Fact]
    public void EveryWarningMustBeAcknowledgedBeforePlanApprovalConfirmation()
    {
        RecoveryPlan plan = WarningPlan();
        IRecoveryWorkflowConfirmationService confirmation =
            A.Fake<IRecoveryWorkflowConfirmationService>();
        A.CallTo(() => confirmation.ConfirmWarningAcknowledgement(
                A<RecoveryIssue>.That.Matches(value => value.Code == "RECOVERY.WARNING.ONE")))
            .Returns(true);
        A.CallTo(() => confirmation.ConfirmWarningAcknowledgement(
                A<RecoveryIssue>.That.Matches(value => value.Code == "RECOVERY.WARNING.TWO")))
            .Returns(false);
        using RecoveryWorkspaceViewModel viewModel = ViewModel(confirmation);
        SetPlan(viewModel, plan);

        viewModel.ApprovePlanCommand.Execute(null);

        viewModel.PlanSummary.Should().Contain("Valid");
        viewModel.Status.Should().Contain("RECOVERY.WARNING.TWO");
        A.CallTo(() => confirmation.ConfirmPlanApproval(A<RecoveryPlan>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public void ApprovalBindsAllIndividuallyAcknowledgedWarnings()
    {
        RecoveryPlan plan = WarningPlan();
        IRecoveryWorkflowConfirmationService confirmation =
            A.Fake<IRecoveryWorkflowConfirmationService>();
        A.CallTo(() => confirmation.ConfirmWarningAcknowledgement(
            A<RecoveryIssue>._)).Returns(true);
        A.CallTo(() => confirmation.ConfirmPlanApproval(plan)).Returns(true);
        using RecoveryWorkspaceViewModel viewModel = ViewModel(confirmation);
        SetPlan(viewModel, plan);

        viewModel.ApprovePlanCommand.Execute(null);

        viewModel.PlanSummary.Should().Contain("Approved");
        A.CallTo(() => confirmation.ConfirmWarningAcknowledgement(
            A<RecoveryIssue>._)).MustHaveHappenedTwiceExactly();
        A.CallTo(() => confirmation.ConfirmPlanApproval(plan))
            .MustHaveHappenedOnceExactly();
    }

    private static RecoveryWorkspaceViewModel ViewModel(
        IRecoveryWorkflowConfirmationService confirmation)
    {
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(DateTimeOffset.UnixEpoch);
        return new(
            new(A.Fake<IRecoverySourceArtifactReader>()),
            new(A.Fake<IRecoveryCurrentStateScanner>(),
                A.Fake<ICurrentStateReconciler>()),
            new(A.Fake<IRecoveryEligibilityValidator>(), clock),
            new(A.Fake<IRecoveryPlanGenerator>(), clock),
            new(clock),
            new(A.Fake<IRecoveryPlanStore>()),
            A.Fake<IPrepareRecoveryExecution>(),
            A.Fake<IExecuteApprovedRecoveryPlan>(),
            A.Fake<ICalibreToolDiscovery>(),
            A.Fake<ICalibreExecutionProfileProvider>(),
            A.Fake<IRecoverySourceFolderPicker>(),
            A.Fake<IExecutionBackupFolderPicker>(),
            A.Fake<IRecoveryPlanFilePicker>(),
            confirmation,
            clock);
    }

    private static void SetPlan(
        RecoveryWorkspaceViewModel viewModel,
        RecoveryPlan plan)
    {
        FieldInfo field = typeof(RecoveryWorkspaceViewModel).GetField(
            "_plan", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Recovery plan field not found.");
        field.SetValue(viewModel, plan);
        viewModel.ApprovePlanCommand.NotifyCanExecuteChanged();
    }

    private static RecoveryPlan WarningPlan()
    {
        CleanupPlanId sourcePlan =
            new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        CleanupExecutionId sourceExecution =
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        RecoveryPlanId planId =
            new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        CleanupPlanContentDigest sourceDigest = new(new string('a', 64));
        string libraryUuid = "44444444-4444-4444-4444-444444444444";
        Sha256Digest full = new(new string('1', 64));
        Sha256Digest affected = new(new string('2', 64));
        Sha256Digest unrelated = new(new string('3', 64));
        RecoveryIssue[] warnings =
        [
            new("RECOVERY.WARNING.ONE", RecoveryIssueSeverity.AcknowledgementRequired,
                "First warning", "Review the first warning."),
            new("RECOVERY.WARNING.TWO", RecoveryIssueSeverity.AcknowledgementRequired,
                "Second warning", "Review the second warning."),
        ];
        CurrentStateReconciliation reconciliation = CurrentStateReconciliation.Create(
            sourceExecution.ToString(), libraryUuid, 27, full, affected,
            unrelated, [], [], []);
        RecoveryInputIdentity input = new(sourcePlan, CleanupPlanSchemaVersion.V1,
            new(1), sourceDigest, sourceExecution,
            "cleanup-execution-journal/1.0", new(new string('4', 64)),
            new string('5', 64), null, VerifiedBackupManifest.Version,
            new(new string('6', 64)), new(new string('7', 64)), "1.0.0",
            new("C:\\calibredb.exe", "9.11.0", new(new string('8', 64)),
                "calibredb/windows/9.11.0"),
            libraryUuid, 27, new(new string('9', 64)), full, affected,
            unrelated, reconciliation.Version, reconciliation.Digest,
            "calibredb/windows/9.11.0/recovery/1.0");
        RecoveryPlanDefinition definition = new(input,
            new(sourcePlan, sourceExecution, DateTimeOffset.UnixEpoch,
                CleanupExecutionDisposition.Completed.ToString(),
                "C:\\backup\\execution"),
            new(sourcePlan, sourceDigest, sourceExecution,
                input.SourceJournalFileDigest,
                input.SourceJournalFinalEntryHash,
                input.OriginalManifestFileDigest,
                input.OriginalManifestInternalDigest, planId, null),
            reconciliation, new([]),
            new([], [], [], [], unrelated), warnings);
        return RecoveryPlanLifecyclePolicy.Create(planId, definition,
            new(warnings, DateTimeOffset.UnixEpoch, input),
            DateTimeOffset.UnixEpoch);
    }
}
