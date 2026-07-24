using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class CleanupExecutionWorkspaceViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreparedPlanRequiresBothAcknowledgementsAndExplicitConfirmation()
    {
        (CleanupPlan plan, LibrarySnapshot snapshot) = Approved();
        IPrepareCleanupExecution prepare = A.Fake<IPrepareCleanupExecution>();
        IExecuteApprovedCleanupPlan execute = A.Fake<IExecuteApprovedCleanupPlan>();
        IExecutionHistoryStore history = A.Fake<IExecutionHistoryStore>();
        IExecutionBackupFolderPicker picker = A.Fake<IExecutionBackupFolderPicker>();
        ICleanupExecutionConfirmationService confirmation = A.Fake<ICleanupExecutionConfirmationService>();
        IClock clock = A.Fake<IClock>();
        CalibreToolDescriptor tool = Tool();
        CleanupExecutionPreparation ready = new(plan, tool,
            CleanupExecutionCapabilityPolicy.Evaluate(plan).Graph, snapshot.Identity.LibraryRoot,
            "C:\\backup", [], Now);
        A.CallTo(() => picker.PickBackupFolder(A<string?>._)).Returns("C:\\backup");
        A.CallTo(() => prepare.ExecuteAsync(A<PrepareCleanupExecutionRequest>._,
            A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._)).Returns(ready);
        A.CallTo(() => confirmation.ConfirmExecution(ready)).Returns(true);
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        A.CallTo(() => history.ReadAsync(snapshot.Identity.CalibreLibraryUuid,
                snapshot.Identity.LibraryRoot, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<ExecutionHistoryEntry>>([]));
        A.CallTo(() => execute.ExecuteAsync(A<ExecuteCleanupPlanRequest>._,
            A<IProgress<CleanupExecutionProgress>?>._, A<CancellationToken>._))
            .Returns(new CleanupExecutionResult(new(Guid.NewGuid()), CleanupExecutionState.Completed,
                CleanupExecutionDisposition.Completed, CleanupExecutionFailureClassification.None, [],
                "C:\\backup\\execution", "journal", new string('d', 64), true));
        CleanupExecutionWorkspaceViewModel viewModel = new(prepare, execute, history, picker, confirmation, clock);
        viewModel.UpdateContext(snapshot, plan, isImported: false);

        viewModel.Effects.Select(value => value.Effect).Should().Contain(["Add format", "Remove format", "Remove record"]);

        viewModel.ChooseBackupCommand.Execute(null);
        await viewModel.PrepareCommand.ExecuteAsync(null);

        viewModel.ExecuteCommand.CanExecute(null).Should().BeFalse();
        viewModel.OtherMutatorsClosed = true;
        viewModel.ExecuteCommand.CanExecute(null).Should().BeFalse();
        viewModel.RecoveryLimitationsAccepted = true;
        viewModel.ExecuteCommand.CanExecute(null).Should().BeTrue();
        await viewModel.ExecuteCommand.ExecuteAsync(null);
        viewModel.ResultSummary.Should().Contain("Completed");
        viewModel.ArtifactSummary.Should().Contain("execution");
        A.CallTo(() => confirmation.ConfirmExecution(ready)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RecoveryRequiredResultIsNeverPresentedAsCompleted()
    {
        (CleanupPlan plan, LibrarySnapshot snapshot) = Approved();
        IPrepareCleanupExecution prepare = A.Fake<IPrepareCleanupExecution>();
        IExecuteApprovedCleanupPlan execute = A.Fake<IExecuteApprovedCleanupPlan>();
        IExecutionHistoryStore history = A.Fake<IExecutionHistoryStore>();
        IExecutionBackupFolderPicker picker = A.Fake<IExecutionBackupFolderPicker>();
        ICleanupExecutionConfirmationService confirmation = A.Fake<ICleanupExecutionConfirmationService>();
        IClock clock = A.Fake<IClock>();
        CleanupExecutionPreparation ready = new(plan, Tool(), CleanupExecutionCapabilityPolicy.Evaluate(plan).Graph,
            snapshot.Identity.LibraryRoot, "C:\\backup", [], Now);
        A.CallTo(() => picker.PickBackupFolder(A<string?>._)).Returns("C:\\backup");
        A.CallTo(() => prepare.ExecuteAsync(A<PrepareCleanupExecutionRequest>._,
            A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._)).Returns(ready);
        A.CallTo(() => confirmation.ConfirmExecution(ready)).Returns(true);
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        A.CallTo(() => history.ReadAsync(snapshot.Identity.CalibreLibraryUuid,
                snapshot.Identity.LibraryRoot, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<ExecutionHistoryEntry>>([]));
        A.CallTo(() => execute.ExecuteAsync(A<ExecuteCleanupPlanRequest>._,
            A<IProgress<CleanupExecutionProgress>?>._, A<CancellationToken>._))
            .Returns(new CleanupExecutionResult(new(Guid.NewGuid()), CleanupExecutionState.RecoveryRequired,
                CleanupExecutionDisposition.RecoveryRequired, CleanupExecutionFailureClassification.DestructiveCommand,
                [new("EXECUTION.DESTRUCTIVE_COMMAND_FAILED", ExecutionIssueSeverity.BlockingError, "Controlled failure.")],
                "C:\\backup\\execution", "journal", new string('d', 64), true));
        CleanupExecutionWorkspaceViewModel viewModel = new(prepare, execute, history, picker, confirmation, clock);
        viewModel.UpdateContext(snapshot, plan, isImported: true);
        viewModel.ChooseBackupCommand.Execute(null);
        await viewModel.PrepareCommand.ExecuteAsync(null);
        viewModel.OtherMutatorsClosed = true;
        viewModel.RecoveryLimitationsAccepted = true;

        await viewModel.ExecuteCommand.ExecuteAsync(null);

        viewModel.Status.Should().StartWith("Recovery Required");
        viewModel.ResultSummary.Should().Contain("Recovery Required").And.NotContain("Completed");
        viewModel.ImportedApprovalNotice.Should().Contain("informational");
        viewModel.Issues.Should().Contain(value => value.Code == "EXECUTION.DESTRUCTIVE_COMMAND_FAILED");
    }

    [Fact]
    public void PlanOrLibraryChangeInvalidatesPreparedExecution()
    {
        (CleanupPlan plan, LibrarySnapshot snapshot) = Approved();
        CleanupExecutionWorkspaceViewModel viewModel = new(A.Fake<IPrepareCleanupExecution>(),
            A.Fake<IExecuteApprovedCleanupPlan>(), A.Fake<IExecutionHistoryStore>(),
            A.Fake<IExecutionBackupFolderPicker>(), A.Fake<ICleanupExecutionConfirmationService>(), A.Fake<IClock>());

        viewModel.UpdateContext(snapshot, plan, false);
        viewModel.PlanSummary.Should().Contain(plan.Id.ToString());
        LibrarySnapshot copiedLibrary = new(new(snapshot.Identity.CalibreLibraryUuid,
            snapshot.Identity.SchemaVersion, "D:\\copied-library"), snapshot.ScannedAt,
            snapshot.Books, snapshot.Findings, snapshot.ExactBinaryDuplicateGroups,
            snapshot.ExactMetadataDuplicateGroups, snapshot.EpubAssessments,
            snapshot.ConsolidationRecommendations);
        viewModel.UpdateSnapshot(copiedLibrary);

        viewModel.ExecuteCommand.CanExecute(null).Should().BeFalse();
        viewModel.PlanSummary.Should().Contain("No cleanup plan");
    }

    private static CalibreToolDescriptor Tool() => new("C:\\trusted\\calibredb.exe",
        new("C:\\trusted\\calibredb.exe", "9.11.0", new(new string('b', 64)), "calibredb/windows/9.11.0"),
        Enum.GetValues<CalibreExecutionCapability>());

    private static (CleanupPlan Plan, LibrarySnapshot Snapshot) Approved()
    {
        FormatFileFingerprint fingerprint = new(5, new(new string('a', 64)));
        FormatFileObservation observation = new(5, Now.AddDays(-1), Now.AddHours(-1), 32);
        CalibreBook[] books =
        [
            new(new(1), "Shared", "Author", [new(new(1), "Author", "Author")], [], [],
                "Author/Shared (1)", new(languages: ["eng"])),
            new(new(2), "Shared", "Author", [new(new(2), "Author", "Author")], [],
                [new("PDF", "book", "Author/Shared (2)/book.pdf", FormatFileStatus.Present, fingerprint, observation)],
                "Author/Shared (2)", new(languages: ["eng"])),
        ];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        LibraryIdentity identity = new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\synthetic\\library");
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            identity, group, books, [], [], [], CancellationToken.None);
        ReviewedConsolidationRecommendation reviewed = ApplyRecommendationOverrideUseCase.Execute(generated, new(
            generated.ModelVersion, generated.InputVersion, RecommendationReviewStatus.Accepted, Now)).Reviewed!;
        LibrarySnapshot snapshot = new(identity, Now, books, [], [], [group], [], [generated]);
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => ids.Create()).Returns(new CleanupPlanId(Guid.Parse("11111111-2222-3333-4444-555555555555")));
        A.CallTo(() => clock.GetUtcNow()).ReturnsNextFromSequence(Now, Now.AddMinutes(1));
        CleanupPlan valid = new GenerateCleanupPlanUseCase(ids, clock).Execute(snapshot, reviewed).Plan!;
        return (new ApproveCleanupPlanUseCase(clock).Execute(valid, snapshot, reviewed).Plan!, snapshot);
    }
}
