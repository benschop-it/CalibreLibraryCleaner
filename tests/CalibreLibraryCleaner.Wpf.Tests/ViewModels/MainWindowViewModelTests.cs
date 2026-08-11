using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.IO;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task StagedScanPublishesExactReadyAndKeepsCandidateCleanupDisabled()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        ILibraryStateSession stateSession = A.Fake<ILibraryStateSession>();
        LibraryState? current = null;
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        A.CallTo(() => stateSession.StartFromExactAnalysisAsync(
                A<LibrarySnapshot>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                LibrarySnapshot snapshot = call.GetArgument<LibrarySnapshot>(0)!;
                current = LibraryState.FromScan(snapshot,
                        new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))
                    .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, snapshot.ScannedAt);
                return Task.FromResult(LibraryStateSessionOutcome.Success(current));
            });
        A.CallTo(() => stateSession.GetCurrent(A<string>.That.IsNotNull())).ReturnsLazily(() => current);
        MainWindowViewModel viewModel = CreateViewModel(
            picker,
            out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader,
            out IFormatFileHasher hasher,
            stateSession: stateSession,
            workflowOptions: LibraryWorkflowOptions.Staged);
        ValidatedLibraryLocation location = new("library", "database");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(
                location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateCatalog()));
        A.CallTo(() => resolver.ResolveFormat(
            location,
            A<string>.That.IsNotNull(),
            A<string>.That.IsNotNull(),
            A<string>.That.IsNotNull()))
            .Returns(ResolvedFormatPathOutcome.Success(new(
                "library", "library/Book/Book.epub", "Book/Book.epub")));
        A.CallTo(() => hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .Returns([FormatHashResult.Failure(
                0, FormatHashResultStatus.Missing, "missing")]);

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);
        await viewModel.ScanCommand.ExecuteAsync(null);

        current!.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.ExactReady);
        viewModel.StatusMessage.Should().StartWith("Exact-only analysis complete");
        viewModel.CandidateCleanupCommand.CanExecute(null).Should().BeFalse();
        A.CallTo(() => stateSession.StartFromExactAnalysisAsync(
            A<LibrarySnapshot>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        A.CallTo(() => stateSession.StartFromScanAsync(
            A<LibrarySnapshot>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task RestartAtCandidatePreparationReadyRestoresCandidateCleanupButton()
    {
        const string libraryRoot = "C:\\Books";
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        LibrarySnapshot snapshot = Snapshot(libraryRoot);
        A.CallTo(() => store.ListAsync(A<CancellationToken>._))
            .Returns([new(libraryRoot, snapshot.ScannedAt)]);
        LibraryState state = LibraryState.FromScan(snapshot,
                new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, snapshot.ScannedAt)
            .AdvanceWorkflow(LibraryWorkflowPhase.CandidatePreparationReady, snapshot.ScannedAt);
        ILibraryStateSession stateSession = A.Fake<ILibraryStateSession>();
        A.CallTo(() => stateSession.LoadAsync(libraryRoot, A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Success(state));
        A.CallTo(() => stateSession.GetCurrent(libraryRoot)).Returns(state);
        ICandidatePreparationWorkflow preparation = A.Fake<ICandidatePreparationWorkflow>();
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(),
            out _,
            out _,
            out _,
            new(store),
            stateSession,
            workflowOptions: LibraryWorkflowOptions.Staged,
            candidatePreparation: preparation);

        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);

        viewModel.CandidateCleanupCommand.CanExecute(null).Should().BeTrue();
        viewModel.CandidateCleanupAutomationName.Should().Be("Prepare Candidate review");
        viewModel.CandidateCleanupToolTip.Should().Contain("without mutation");
    }

    [Fact]
    public async Task FirstCandidateActivationPreparesReviewAndNeverMutates()
    {
        const string libraryRoot = "C:\\Books";
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        LibrarySnapshot exactSnapshot = Snapshot(libraryRoot);
        LibrarySnapshot unifiedSnapshot = UnifiedSnapshot(libraryRoot);
        A.CallTo(() => store.ListAsync(A<CancellationToken>._))
            .Returns([new(libraryRoot, exactSnapshot.ScannedAt)]);
        ILibraryStateSession stateSession = A.Fake<ILibraryStateSession>();
        LibraryState current = WorkflowState(
            exactSnapshot, LibraryWorkflowPhase.CandidatePreparationReady, source: null);
        A.CallTo(() => stateSession.LoadAsync(libraryRoot, A<CancellationToken>._))
            .ReturnsLazily(() => LibraryStateSessionOutcome.Success(current));
        A.CallTo(() => stateSession.GetCurrent(libraryRoot)).ReturnsLazily(() => current);
        ICandidatePreparationWorkflow preparation = A.Fake<ICandidatePreparationWorkflow>();
        LibraryWorkflowSource source = new(current.GenerationId, current.Revision);
        LibraryState refreshed = WorkflowState(
            exactSnapshot, LibraryWorkflowPhase.CandidatePreparationReady, source);
        LibraryState analyzed = WorkflowState(
            unifiedSnapshot, LibraryWorkflowPhase.CandidateAnalysisReady, source,
            refreshed.GenerationId);
        TaskCompletionSource heartbeatObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => preparation.RefreshAsync(
            libraryRoot, A<IProgress<CandidatePreparationProgress>?>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                call.GetArgument<IProgress<CandidatePreparationProgress>?>(1)?.Report(new(
                    CandidatePreparationPhase.AssessingEpubFormats,
                    25,
                    100,
                    CandidateProgressUnit.Files,
                    "Assessing EPUB files: 25 of 100 complete",
                    "Content: 4",
                    4));
                await heartbeatObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
                current = refreshed;
                return new PostExactRefreshResult(
                    refreshed,
                    new(1, 0, 0, 0L, 0, 0, 0, 0L, 0, 0, 0, 1));
            });
        A.CallTo(() => preparation.AnalyzeAsync(
            libraryRoot, A<IProgress<CandidatePreparationProgress>?>._, A<CancellationToken>._))
            .ReturnsLazily(() =>
            {
                current = analyzed;
                return new ResidualCandidateAnalysisResult(analyzed, 1, 1, 1, false);
            });
        IUnifiedCandidateCleanupExecutor executor = A.Fake<IUnifiedCandidateCleanupExecutor>();
        IUnifiedCandidateCleanupConfirmationService confirmation =
            A.Fake<IUnifiedCandidateCleanupConfirmationService>();
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), stateSession,
            workflowOptions: LibraryWorkflowOptions.Staged,
            candidatePreparation: preparation,
            candidateExecutor: executor,
            candidateConfirmation: confirmation);
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);
        ConcurrentQueue<string> candidateStatuses = [];
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(MainWindowViewModel.StatusMessage))
            {
                candidateStatuses.Enqueue(viewModel.StatusMessage);
                if (viewModel.StatusMessage.Contains("Elapsed 0:01", StringComparison.Ordinal))
                    heartbeatObserved.TrySetResult();
            }
        };

        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);

        A.CallTo(() => preparation.RefreshAsync(
            libraryRoot, A<IProgress<CandidatePreparationProgress>?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => preparation.AnalyzeAsync(
            libraryRoot, A<IProgress<CandidatePreparationProgress>?>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => confirmation.ConfirmExternalBackup(A<int>._)).MustNotHaveHappened();
        A.CallTo(() => executor.ExecuteAsync(
            A<ExecuteUnifiedCandidateCleanupRequest>._,
            A<IProgress<UnifiedCandidateCleanupProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        candidateStatuses.Should().Contain(value =>
            value.Contains("Assessing EPUB files: 25 of 100 complete", StringComparison.Ordinal)
            && value.Contains("Content: 4", StringComparison.Ordinal));
        candidateStatuses.Should().Contain(value =>
            value.Contains("Assessing EPUB files: 25 of 100 complete", StringComparison.Ordinal)
            && value.Contains("Elapsed 0:01", StringComparison.Ordinal));
        viewModel.UnifiedCandidateGroups.Should().ContainSingle();
        viewModel.StatusMessage.Should().Contain("activate Candidate cleanup again");
        viewModel.CandidateCleanupAutomationName.Should().Be("Run Candidate cleanup");
        viewModel.CandidateCleanupCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task CandidatePreparationCancelUpdatesImmediatelyAndDoesNotAnalyze()
    {
        const string libraryRoot = "C:\\Books";
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        LibrarySnapshot snapshot = Snapshot(libraryRoot);
        A.CallTo(() => store.ListAsync(A<CancellationToken>._))
            .Returns([new(libraryRoot, snapshot.ScannedAt)]);
        LibraryState state = WorkflowState(
            snapshot, LibraryWorkflowPhase.CandidatePreparationReady, source: null);
        ILibraryStateSession stateSession = A.Fake<ILibraryStateSession>();
        A.CallTo(() => stateSession.LoadAsync(libraryRoot, A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Success(state));
        A.CallTo(() => stateSession.GetCurrent(libraryRoot)).Returns(state);
        ICandidatePreparationWorkflow preparation = A.Fake<ICandidatePreparationWorkflow>();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => preparation.RefreshAsync(
                libraryRoot,
                A<IProgress<CandidatePreparationProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                CancellationToken token = call.GetArgument<CancellationToken>(2);
                call.GetArgument<IProgress<CandidatePreparationProgress>?>(1)?.Report(new(
                    CandidatePreparationPhase.AssessingEpubFormats,
                    1,
                    100,
                    CandidateProgressUnit.Files,
                    "Assessing EPUB files: 1 of 100 complete"));
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("The cancellation test reached an unreachable path.");
            });
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), stateSession,
            workflowOptions: LibraryWorkflowOptions.Staged,
            candidatePreparation: preparation);
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);

        Task operation = viewModel.CandidateCleanupCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        viewModel.CancelCommand.Execute(null);

        viewModel.StatusMessage.Should().Be(
            "Cancel requested; waiting for the current bounded operation to stop...");
        await operation;
        viewModel.StatusMessage.Should().Be("Candidate preparation canceled without mutation.");
        A.CallTo(() => preparation.AnalyzeAsync(
            A<string>._,
            A<IProgress<CandidatePreparationProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
        stateSession.GetCurrent(libraryRoot).Should().BeSameAs(state);
    }

    [Fact]
    public async Task RestartedCandidateReviewExecutesSelectionsOnceAndDisablesAtCompleted()
    {
        const string libraryRoot = "C:\\Books";
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        LibrarySnapshot snapshot = UnifiedSnapshot(libraryRoot);
        A.CallTo(() => store.ListAsync(A<CancellationToken>._))
            .Returns([new(libraryRoot, snapshot.ScannedAt)]);
        LibraryWorkflowSource source = new(
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")), new(3));
        LibraryState analyzed = WorkflowState(snapshot, LibraryWorkflowPhase.CandidateAnalysisReady, source);
        LibraryState current = analyzed;
        ILibraryStateSession stateSession = A.Fake<ILibraryStateSession>();
        A.CallTo(() => stateSession.LoadAsync(libraryRoot, A<CancellationToken>._))
            .ReturnsLazily(() => LibraryStateSessionOutcome.Success(current));
        A.CallTo(() => stateSession.GetCurrent(libraryRoot)).ReturnsLazily(() => current);
        ICandidatePreparationWorkflow preparation = A.Fake<ICandidatePreparationWorkflow>();
        IUnifiedCandidateCleanupExecutor executor = A.Fake<IUnifiedCandidateCleanupExecutor>();
        IUnifiedCandidateCleanupConfirmationService confirmation =
            A.Fake<IUnifiedCandidateCleanupConfirmationService>();
        A.CallTo(() => confirmation.ConfirmExternalBackup(1)).Returns(true);
        ExecuteUnifiedCandidateCleanupRequest? captured = null;
        A.CallTo(() => executor.ExecuteAsync(
                A<ExecuteUnifiedCandidateCleanupRequest>._,
                A<IProgress<UnifiedCandidateCleanupProgress>?>._,
                A<CancellationToken>._))
            .Invokes(call => captured = call.GetArgument<ExecuteUnifiedCandidateCleanupRequest>(0))
            .ReturnsLazily(() =>
            {
                current = analyzed.AdvanceWorkflow(
                    LibraryWorkflowPhase.Completed, analyzed.ProjectedAtUtc);
                return new UnifiedCandidateCleanupResult(
                    new(Guid.Parse("99999999-8888-7777-6666-555555555555")),
                    UnifiedCandidateCleanupState.Completed,
                    0,
                    1,
                    1,
                    0,
                    []);
            });
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), stateSession,
            workflowOptions: LibraryWorkflowOptions.Staged,
            candidatePreparation: preparation,
            candidateExecutor: executor,
            candidateConfirmation: confirmation);
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);
        UnifiedCandidateGroupRowViewModel group = viewModel.UnifiedCandidateGroups.Single();
        viewModel.SelectedUnifiedCandidateMember = group.Members.Single(value => value.BookId == 1);

        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);

        A.CallTo(() => preparation.RefreshAsync(
            A<string>._, A<IProgress<CandidatePreparationProgress>?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => preparation.AnalyzeAsync(
            A<string>._, A<IProgress<CandidatePreparationProgress>?>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => confirmation.ConfirmExternalBackup(1)).MustHaveHappenedOnceExactly();
        captured.Should().NotBeNull();
        captured!.ExpectedGeneration.Should().Be(analyzed.GenerationId);
        captured.ExpectedRevision.Should().Be(analyzed.Revision);
        captured.ExternalBackupConfirmed.Should().BeTrue();
        captured.GroupSelections.Should().ContainSingle(value =>
            value.KeeperBookId == new CalibreBookId(1)
            && value.KeeperWasOverridden
            && !value.Skip);
        viewModel.CandidateCleanupCommand.CanExecute(null).Should().BeFalse();
        viewModel.CandidateCleanupAutomationName.Should().Be("Candidate cleanup completed");
        viewModel.CandidateCleanupResultSummary.Should().Contain("removed 1 format");
    }

    [Fact]
    public async Task PersistedLibrarySelectionLoadsPreviousSnapshot()
    {
        const string libraryRoot = "C:\\Books";
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        LibrarySnapshot snapshot = Snapshot(libraryRoot);
        A.CallTo(() => store.ListAsync(A<CancellationToken>._))
            .Returns([new(libraryRoot, snapshot.ScannedAt)]);
        A.CallTo(() => store.ReadAsync(libraryRoot, A<CancellationToken>._)).Returns(snapshot);
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store));

        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);

        viewModel.PersistedLibraryPaths.Should().Equal(libraryRoot);
        viewModel.SelectedLibraryPath.Should().Be(libraryRoot);
        viewModel.Books.Should().ContainSingle(book => book.Title == "Persisted Book");
        viewModel.StatusMessage.Should().Contain("Loaded persisted scan").And.Contain("may be stale");
        viewModel.ExpandedCandidateSummary.Should().Contain("Run a fresh scan");
    }

    [Fact]
    public async Task PersistedExpandedGroupsShowKeeperActionsAndOpenSelectedMember()
    {
        const string libraryRoot = "C:\\Books";
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        IEbookViewerLauncher viewer = A.Fake<IEbookViewerLauncher>();
        LibrarySnapshot snapshot = ExpandedSnapshot(libraryRoot);
        A.CallTo(() => store.ListAsync(A<CancellationToken>._))
            .Returns([new(libraryRoot, snapshot.ScannedAt)]);
        A.CallTo(() => store.ReadAsync(libraryRoot, A<CancellationToken>._)).Returns(snapshot);
        A.CallTo(() => viewer.LaunchAsync(A<EbookViewerLaunchRequest>._, A<CancellationToken>._))
            .Returns(EbookViewerLaunchResult.Success());
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), ebookViewer: viewer);

        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);

        viewModel.ExpandedCandidateGroups.Should().ContainSingle();
        viewModel.ExpandedCandidateGroups[0].Eligibility.Should().Be("To be reviewed");
        viewModel.ExpandedCandidateGroups[0].RequiresReview.Should().BeTrue();
        viewModel.ExpandedCandidateGroups[0].Skip.Should().BeFalse();
        viewModel.ExpandedCandidateSummary.Should().Contain("1 expanded content-confirmed groups");
        viewModel.SelectedExpandedCandidateMembers.Should().HaveCount(2);
        viewModel.SelectedExpandedCandidateGroup!.KeeperRecordId.Should().Be(2);
        viewModel.SelectedExpandedCandidateMember!.BookId.Should().Be(2);
        viewModel.SelectedExpandedCandidateMembers[1].Action.Should().Be("Keep");
        viewModel.SelectedExpandedCandidateMembers[0].Action.Should().Be("Remove");
        viewModel.SelectedExpandedCandidateMember = viewModel.SelectedExpandedCandidateMembers[0];
        viewModel.SelectedExpandedCandidateMembers[0].Action.Should().Be("Keep");
        viewModel.SelectedExpandedCandidateMembers[1].Action.Should().Be("Remove");
        viewModel.SelectedExpandedCandidateMember = viewModel.SelectedExpandedCandidateMembers[1];
        await viewModel.OpenSelectedExpandedCandidateCommand.ExecuteAsync(null);
        A.CallTo(() => viewer.LaunchAsync(
            A<EbookViewerLaunchRequest>.That.Matches(value =>
                value.LibraryRoot == libraryRoot
                && value.ExpectedRelativePath == "Author/Second.epub"),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PersistedUnifiedGroupsShowKeeperDetailsOverrideAndOpenSelectedMember()
    {
        const string libraryRoot = "C:\\Books";
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        IEbookViewerLauncher viewer = A.Fake<IEbookViewerLauncher>();
        LibrarySnapshot snapshot = UnifiedSnapshot(libraryRoot);
        A.CallTo(() => store.ListAsync(A<CancellationToken>._))
            .Returns([new(libraryRoot, snapshot.ScannedAt)]);
        A.CallTo(() => store.ReadAsync(libraryRoot, A<CancellationToken>._)).Returns(snapshot);
        A.CallTo(() => viewer.LaunchAsync(A<EbookViewerLaunchRequest>._, A<CancellationToken>._))
            .Returns(EbookViewerLaunchResult.Success());
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), ebookViewer: viewer);

        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);

        viewModel.UnifiedCandidateGroups.Should().ContainSingle();
        UnifiedCandidateGroupRowViewModel group = viewModel.UnifiedCandidateGroups[0];
        group.Skip.Should().BeFalse();
        group.KeeperTitle.Should().Be("Second");
        group.KeeperAuthors.Should().Be("Author");
        viewModel.UnifiedCandidateSummary.Should().Contain("1 unified candidate group");
        UnifiedCandidateMemberRowViewModel alternate = group.Members.Single(value => value.BookId == 1);
        viewModel.SelectedUnifiedCandidateMember = alternate;
        group.KeeperRecordId.Should().Be(1);
        group.KeeperTitle.Should().Be("First");
        alternate.Action.Should().Be("Keep");

        await viewModel.OpenSelectedUnifiedCandidateCommand.ExecuteAsync(null);

        A.CallTo(() => viewer.LaunchAsync(
            A<EbookViewerLaunchRequest>.That.Matches(value =>
                value.LibraryRoot == libraryRoot
                && value.ExpectedRelativePath == "Author/First.epub"),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PersistedAuthoritativeStateLoadsAsMutationEligible()
    {
        const string libraryRoot = "C:\\Books";
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        LibrarySnapshot snapshot = Snapshot(libraryRoot);
        A.CallTo(() => store.ListAsync(A<CancellationToken>._))
            .Returns([new(libraryRoot, snapshot.ScannedAt)]);
        ILibraryStateSession stateSession = A.Fake<ILibraryStateSession>();
        LibraryState state = new(new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
            new(7), LibraryStateStatus.Authoritative, snapshot, snapshot.ScannedAt.AddMinutes(1));
        A.CallTo(() => stateSession.LoadAsync(libraryRoot, A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Success(state));
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), stateSession);

        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);

        viewModel.StatusMessage.Should().Contain("authoritative projected state revision 7")
            .And.Contain("External Calibre changes require Rescan");
        viewModel.Books.Should().ContainSingle(value => value.Title == "Persisted Book");
        A.CallTo(() => store.DeleteAsync(libraryRoot, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PersistedLoadShowsProgressBeforeStateDeserializationCompletes()
    {
        const string libraryRoot = "C:\\Books";
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        LibrarySnapshot snapshot = Snapshot(libraryRoot);
        A.CallTo(() => store.ListAsync(A<CancellationToken>._))
            .Returns([new(libraryRoot, snapshot.ScannedAt)]);
        ILibraryStateSession stateSession = A.Fake<ILibraryStateSession>();
        TaskCompletionSource<LibraryStateSessionOutcome> pendingLoad = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => stateSession.LoadAsync(libraryRoot, A<CancellationToken>._))
            .Returns(pendingLoad.Task);
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), stateSession);
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;

        Task load = viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.StatusMessage == "Loading saved library analysis...");

        viewModel.IsBusy.Should().BeTrue();
        viewModel.IsProgressIndeterminate.Should().BeTrue();
        viewModel.ProgressPercentage.Should().Be(0);
        viewModel.Books.Should().BeEmpty();

        pendingLoad.SetResult(LibraryStateSessionOutcome.Success(new(
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
            new(7), LibraryStateStatus.Authoritative, snapshot, snapshot.ScannedAt.AddMinutes(1))));
        await load;

        viewModel.IsProgressIndeterminate.Should().BeFalse();
        viewModel.ProgressPercentage.Should().Be(100);
        viewModel.Books.Should().ContainSingle();
    }

    [Fact]
    public async Task LegacyDevelopmentSnapshotMigratesToStateBeforeDeletion()
    {
        const string libraryRoot = @"C:\Books";
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        LibrarySnapshot snapshot = Snapshot(libraryRoot);
        A.CallTo(() => store.ListAsync(A<CancellationToken>._))
            .Returns([new(libraryRoot, snapshot.ScannedAt)]);
        A.CallTo(() => store.ReadAsync(libraryRoot, A<CancellationToken>._)).Returns(snapshot);
        ILibraryStateSession stateSession = A.Fake<ILibraryStateSession>();
        A.CallTo(() => stateSession.LoadAsync(libraryRoot, A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Failure(
                "LIBRARY_STATE.NOT_FOUND", "No persisted authoritative library state exists."));
        A.CallTo(() => stateSession.StartFromScanAsync(snapshot, A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Success(LibraryState.FromScan(snapshot,
                new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))));
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), stateSession);

        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);

        viewModel.StatusMessage.Should().Contain("Migrated and loaded trusted development snapshot")
            .And.Contain("authoritative projected state revision 0");
        A.CallTo(() => stateSession.StartFromScanAsync(snapshot, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => store.DeleteAsync(libraryRoot, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task SuccessfulScanPersistsStateWithoutWritingDuplicateSnapshot()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        ILibraryStateSession stateSession = A.Fake<ILibraryStateSession>();
        A.CallTo(() => stateSession.StartFromScanAsync(
                A<LibrarySnapshot>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                LibrarySnapshot snapshot = call.GetArgument<LibrarySnapshot>(0)!;
                return Task.FromResult(LibraryStateSessionOutcome.Success(LibraryState.FromScan(snapshot,
                    new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")))));
            });
        A.CallTo(() => store.ListAsync(A<CancellationToken>._)).Returns([]);
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        MainWindowViewModel viewModel = CreateViewModel(
            picker,
            out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader,
            out IFormatFileHasher hasher,
            new(store),
            stateSession);
        ValidatedLibraryLocation location = new("library", "database");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateCatalog()));
        A.CallTo(() => resolver.ResolveFormat(
            location,
            A<string>.That.IsNotNull(),
            A<string>.That.IsNotNull(),
            A<string>.That.IsNotNull()))
            .Returns(ResolvedFormatPathOutcome.Success(new("library", "full", "Book/Book.epub")));
        A.CallTo(() => hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .Returns([FormatHashResult.Failure(0, FormatHashResultStatus.Missing, "missing")]);

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);
        await viewModel.ScanCommand.ExecuteAsync(null);

        A.CallTo(() => store.WriteAsync(A<LibrarySnapshot>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        A.CallTo(() => stateSession.StartFromScanAsync(
            A<LibrarySnapshot>.That.Matches(value => value.Identity.LibraryRoot == "library"),
            A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task PickerCancellationLeavesSelectionUnchanged()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns(null);
        MainWindowViewModel viewModel = CreateViewModel(picker, out _, out _, out _);

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);

        viewModel.SelectedLibraryPath.Should().BeEmpty();
    }

    [Fact]
    public async Task SuccessfulScanDisplaysBooksAndMissingFormats()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        MainWindowViewModel viewModel = CreateViewModel(
            picker,
            out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader,
            out IFormatFileHasher hasher);
        ValidatedLibraryLocation location = new("library", "database");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateCatalog()));
        A.CallTo(() => resolver.ResolveFormat(
                location,
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull()))
            .Returns(ResolvedFormatPathOutcome.Success(new("library", "full", "Book/Book.epub")));
        A.CallTo(() => hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<FormatHashResult>>(
                [FormatHashResult.Failure(0, FormatHashResultStatus.Missing, "FileNotFound")]));
        (bool IsIndeterminate, double Percentage)? preparingProgress = null;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(MainWindowViewModel.StatusMessage)
                && viewModel.StatusMessage.StartsWith("Preparing ", StringComparison.Ordinal))
            {
                preparingProgress = (viewModel.IsProgressIndeterminate, viewModel.ProgressPercentage);
            }
        };

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);
        await viewModel.ScanCommand.ExecuteAsync(null);

        preparingProgress.Should().Be((false, 0d));
        viewModel.Books.Should().ContainSingle();
        viewModel.SelectedFormats.Should().ContainSingle(format => format.Status == "Missing");
        viewModel.StatusMessage.Should().Contain("1 missing format files");
        viewModel.IsProgressIndeterminate.Should().BeFalse();
        viewModel.ProgressPercentage.Should().Be(100);
        viewModel.ExactDuplicateSummary.Should().Contain("No exact file duplicate groups");
    }

    [Fact]
    public async Task EpubAssessmentDisplaysVersionsScoreDisqualificationAndSeverityFilters()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
        ICalibreMetadataReader reader = A.Fake<ICalibreMetadataReader>();
        IFormatFileHasher hasher = A.Fake<IFormatFileHasher>();
        IEpubInspector inspector = A.Fake<IEpubInspector>();
        IClock clock = A.Fake<IClock>();
        ValidatedLibraryLocation location = new("library", "database");
        FormatFileFingerprint fingerprint = new(10, new Sha256Digest(new string('a', 64)));
        FormatFileObservation observation = new(10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0);
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._)).Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateCatalog()));
        A.CallTo(() => resolver.ResolveFormat(
                location,
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull(),
                "EPUB"))
            .Returns(ResolvedFormatPathOutcome.Success(new("library", "full", "Book/Book.epub")));
        A.CallTo(() => hasher.HashAsync(A<IReadOnlyList<FormatHashRequest>>._, A<int>._, A<IProgress<FormatHashProgress>?>._, A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<FormatHashResult>>([FormatHashResult.Success(0, fingerprint, observation)]));
        A.CallTo(() => inspector.InspectAsync(A<EpubInspectionRequest>._, A<IProgress<EpubInspectionProgress>?>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                EpubInspectionRequest request = call.GetArgument<EpubInspectionRequest>(0)!;
                return Task.FromResult(new EpubInspectionResult(
                    request.BookId, request.ExpectedRelativePath, true, true, true, "3.0", "Embedded title",
                    ["Author"], ["en"], ["2020-01-01"], ["9780306406157"], true, 600, 800, true,
                    4, 1, 1, 4, [], [], [], [], ["https://example.invalid/remote.css"], 6_000, "None", false, []));
            });
        MainWindowViewModel viewModel = new(
            new ValidateLibraryUseCase(resolver),
            new ScanLibraryUseCase(
                resolver,
                reader,
                hasher,
                clock,
                new(),
                new AssessEpubFormatsUseCase(inspector, new())),
            picker);

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.EpubAssessments.Should().ContainSingle();
        viewModel.SelectedEpubAssessment!.Score.Should().Be("100");
        viewModel.SelectedEpubAssessment.AnalyzerVersion.Should().Be("epub-inspector/1.0.5");
        viewModel.SelectedEpubAssessment.ScoringModelVersion.Should().Be("epub-quality/1.0.3");
        viewModel.SelectedEpubFeatureSummary.Should().Contain("Readable characters: 6000");
        viewModel.SelectedEpubFeatureSummary.Should().Contain("Dates: 2020-01-01");
        viewModel.SelectedEpubFeatureSummary.Should().Contain("Strong identifiers: 9780306406157");
        viewModel.SelectedEpubFeatureSummary.Should().Contain("Local resources: 4");
        viewModel.EpubFindingFilterMode = EpubFindingFilterMode.Information;
        viewModel.EpubFindings.Should().OnlyContain(finding => finding.Severity == "Information");

        A.CallTo(() => inspector.InspectAsync(A<EpubInspectionRequest>._, A<IProgress<EpubInspectionProgress>?>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                EpubInspectionRequest request = call.GetArgument<EpubInspectionRequest>(0)!;
                return Task.FromResult(EpubInspectionResult.Failed(
                    request.BookId,
                    request.ExpectedRelativePath,
                    EpubInspectionProblemCode.Encrypted,
                    "Unsupported encryption prevents inspection."));
            });

        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.SelectedEpubAssessment!.Status.Should().Be("Unassessed");
        viewModel.SelectedEpubAssessment.Score.Should().Be("Not scored — unassessed");
        viewModel.EpubAssessmentStatusMessage.Should().Contain("may still open in Calibre").And.NotContain("disqualified");
        viewModel.EpubFindingFilterMode = EpubFindingFilterMode.Warning;
        viewModel.EpubFindings.Should().ContainSingle(finding => finding.RuleId == "EPUB.ENCRYPTION");
    }

    [Fact]
    public async Task CancellationShowsNeutralStateAndAllowsRetry()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        MainWindowViewModel viewModel = CreateViewModel(
            picker,
            out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader,
            out _);
        ValidatedLibraryLocation location = new("library", "database");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .ReturnsLazily(call => WaitForCancellation(call.GetArgument<CancellationToken>(2)));
        await viewModel.SelectLibraryCommand.ExecuteAsync(null);

        Task scan = viewModel.ScanCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsBusy);
        viewModel.CancelCommand.Execute(null);
        await scan;

        viewModel.StatusMessage.Should().Contain("canceled");
        viewModel.ErrorMessage.Should().BeEmpty();
        viewModel.IsBusy.Should().BeFalse();
        viewModel.ScanCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task SuccessfulScanDisplaysExactFileGroupMembers()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        IEbookViewerLauncher viewer = A.Fake<IEbookViewerLauncher>();
        A.CallTo(() => viewer.LaunchAsync(A<EbookViewerLaunchRequest>._, A<CancellationToken>._))
            .Returns(EbookViewerLaunchResult.Success());
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        MainWindowViewModel viewModel = CreateViewModel(
            picker,
            out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader,
            out IFormatFileHasher hasher,
            ebookViewer: viewer);
        ValidatedLibraryLocation location = new("library", "database");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateCatalog(bookCount: 2, secondBookHasPdf: true)));
        A.CallTo(() => resolver.ResolveFormat(
                location,
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull()))
            .ReturnsLazily(call =>
            {
                string directory = call.GetArgument<string>(1)!;
                return ResolvedFormatPathOutcome.Success(new("library", directory, $"{directory}/Book.epub"));
            });
        FormatFileFingerprint fingerprint = new(4, new Sha256Digest(new string('d', 64)));
        FormatFileFingerprint pdfFingerprint = new(5, new Sha256Digest(new string('e', 64)));
        A.CallTo(() => hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<IReadOnlyList<FormatHashResult>>(
                call.GetArgument<IReadOnlyList<FormatHashRequest>>(0)!
                    .Select(request => Successful(request.Sequence,
                        request.Format == "EPUB" ? fingerprint : pdfFingerprint))
                    .ToArray()));

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.ExactDuplicateGroups.Should().ContainSingle();
        viewModel.SelectedExactDuplicateMembers.Should().HaveCount(2);
        viewModel.ExactDuplicateGroups[0].RecordCount.Should().Be(2);
        viewModel.ExactDuplicateSummary.Should().Contain("1 exact file duplicate group");

        ExactDuplicateMemberRowViewModel first = viewModel.SelectedExactDuplicateMembers[0];
        ExactDuplicateMemberRowViewModel second = viewModel.SelectedExactDuplicateMembers[1];
        viewModel.RetainedExactDuplicateMember.Should().BeSameAs(second);
        first.CleanupAction.Should().Be("Remove format");
        second.CleanupAction.Should().Be("Keep");
        viewModel.ExactDuplicateGroups[0].RecordIdsToDelete.Should().Equal(new CalibreBookId(1));

        viewModel.SelectedExactDuplicateMember = first;
        await viewModel.OpenSelectedExactDuplicateCommand.ExecuteAsync(null);

        viewModel.RetainedExactDuplicateMember.Should().BeSameAs(first);
        first.CleanupAction.Should().Be("Keep");
        second.CleanupAction.Should().Be("Remove format");
        viewModel.ExactDuplicateGroups[0].RecordIdsToDelete.Should().BeEmpty();
        A.CallTo(() => viewer.LaunchAsync(
            A<EbookViewerLaunchRequest>.That.Matches(value =>
                value.LibraryRoot == "library"
                && value.ExpectedRelativePath == first.ExpectedRelativePath),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        viewModel.StatusMessage.Should().Contain("Opened selected EPUB");
    }

    [Fact]
    public async Task MetadataGroupsSupportFilteringNavigationAndSessionDeferWithoutIntegrationCalls()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        IEbookViewerLauncher viewer = A.Fake<IEbookViewerLauncher>();
        A.CallTo(() => viewer.LaunchAsync(A<EbookViewerLaunchRequest>._, A<CancellationToken>._))
            .Returns(EbookViewerLaunchResult.Success());
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        MainWindowViewModel viewModel = CreateViewModel(
            picker,
            out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader,
            out IFormatFileHasher hasher,
            ebookViewer: viewer);
        ValidatedLibraryLocation location = new("library", "database");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateMetadataDuplicateCatalog()));
        A.CallTo(() => resolver.ResolveFormat(
                location,
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull()))
            .ReturnsLazily(call =>
            {
                string directory = call.GetArgument<string>(1)!;
                return ResolvedFormatPathOutcome.Success(new("library", directory, $"{directory}/Book.epub"));
            });
        A.CallTo(() => hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<IReadOnlyList<FormatHashResult>>(
                call.GetArgument<IReadOnlyList<FormatHashRequest>>(0)!
                    .Select(request => Successful(
                        request.Sequence,
                        new FormatFileFingerprint(
                            request.Sequence + 1,
                            new Sha256Digest(new string((char)('a' + request.Sequence), 64)))))
                    .ToArray()));

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.MetadataDuplicateGroups.Should().HaveCount(2);
        viewModel.StatusMessage.Should().Contain("2 exact metadata candidate groups");
        viewModel.MetadataDuplicateSummary.Should().Contain("2 of 2 metadata candidate groups visible");
        viewModel.SelectedMetadataDuplicateGroup!.NormalizedTitle.Should().Be("ALPHA:BOOK");
        viewModel.SelectedMetadataDuplicateMember.Should().BeSameAs(
            viewModel.SelectedMetadataDuplicateGroup.KeeperMember);
        MetadataDuplicateMemberRowViewModel alternateKeeper = viewModel.SelectedMetadataDuplicateMembers
            .Single(value => !value.IsKeeper);
        viewModel.SelectedMetadataDuplicateMember = alternateKeeper;
        alternateKeeper.Action.Should().Be("Keep");
        viewModel.SelectedMetadataDuplicateMembers.Should().ContainSingle(value => value.Action == "Keep");
        await viewModel.OpenSelectedMetadataCandidateCommand.ExecuteAsync(null);
        A.CallTo(() => viewer.LaunchAsync(
            A<EbookViewerLaunchRequest>.That.Matches(value =>
                value.LibraryRoot == "library"
                && value.ExpectedRelativePath == alternateKeeper.LaunchRelativePath),
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
        viewModel.NextMetadataDuplicateGroupCommand.Execute(null);
        viewModel.SelectedMetadataDuplicateGroup.NormalizedTitle.Should().Be("BETA BOOK");
        viewModel.PreviousMetadataDuplicateGroupCommand.Execute(null);
        viewModel.SelectedMetadataDuplicateGroup.NormalizedTitle.Should().Be("ALPHA:BOOK");

        Fake.ClearRecordedCalls(resolver);
        Fake.ClearRecordedCalls(reader);
        Fake.ClearRecordedCalls(hasher);
        viewModel.MetadataDuplicateFilterText = "beta";
        viewModel.MetadataDuplicateGroups.Should().ContainSingle();
        viewModel.SelectedMetadataDuplicateGroup!.NormalizedTitle.Should().Be("BETA BOOK");
        RecommendationFormatRowViewModel formatReview = viewModel.SelectedRecommendationFormats.Single();
        formatReview.ReviewedSource = formatReview.SourceOptions.Single(value => value.Action == "ExcludeFinalFormat");
        viewModel.ToggleMetadataDuplicateDeferredCommand.Execute(null);
        viewModel.SelectedMetadataDuplicateGroup!.IsDeferred.Should().BeTrue();
        viewModel.SelectedMetadataDuplicateGroup.Reviewed!.CurrentOverride!.FormatOverrides
            .Should().ContainSingle(value => value.Action == FormatOverrideAction.ExcludeFinalFormat);
        viewModel.MetadataDuplicateFilterMode = MetadataDuplicateFilterMode.Active;
        viewModel.MetadataDuplicateGroups.Should().BeEmpty();
        viewModel.MetadataDuplicateFilterMode = MetadataDuplicateFilterMode.Deferred;
        viewModel.MetadataDuplicateGroups.Should().ContainSingle(group => group.IsDeferred);
        Fake.GetCalls(resolver).Should().BeEmpty();
        Fake.GetCalls(reader).Should().BeEmpty();
        Fake.GetCalls(hasher).Should().BeEmpty();

        viewModel.MetadataDuplicateFilterText = string.Empty;
        viewModel.MetadataDuplicateFilterMode = MetadataDuplicateFilterMode.All;
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.MetadataDuplicateGroups.Should().ContainSingle(
            group => group.NormalizedTitle == "BETA BOOK" && group.IsDeferred);
        viewModel.MetadataDuplicateGroups.Should().ContainSingle(
            group => group.NormalizedTitle == "ALPHA:BOOK" && !group.IsDeferred);
        viewModel.SelectedMetadataDuplicateMembers.Should().HaveCount(2);
        viewModel.MetadataDuplicateGroups[0].Reason.Should().Contain("exactly equal");

        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateMetadataDuplicateCatalog("different-library-uuid")));
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.MetadataDuplicateGroups.Should().OnlyContain(group => !group.IsDeferred);
    }

    [Fact]
    public async Task SyntheticLibraryFlowsThroughRealInfrastructureToDuplicateView()
    {
        using SyntheticDuplicateLibrary library = new();
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns(library.RootPath);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        services.AddSingleton(new LibraryAnalysisOptions(maxHashConcurrency: 2));
        services.AddSingleton<EpubAssessmentEngine>();
        services.AddSingleton<AssessEpubFormatsUseCase>();
        using ServiceProvider provider = services.BuildServiceProvider();
        MainWindowViewModel viewModel = new(
            new ValidateLibraryUseCase(provider.GetRequiredService<ILibraryPathResolver>()),
            new ScanLibraryUseCase(
                provider.GetRequiredService<ILibraryPathResolver>(),
                provider.GetRequiredService<ICalibreMetadataReader>(),
                provider.GetRequiredService<IFormatFileHasher>(),
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<LibraryAnalysisOptions>(),
                provider.GetRequiredService<AssessEpubFormatsUseCase>()),
            picker);
        int bookCollectionChanges = 0;
        int groupCollectionChanges = 0;
        ((INotifyCollectionChanged)viewModel.Books).CollectionChanged += (_, _) => bookCollectionChanges++;
        ((INotifyCollectionChanged)viewModel.ExactDuplicateGroups).CollectionChanged += (_, _) => groupCollectionChanges++;

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().BeEmpty();
        viewModel.Books.Should().HaveCount(2);
        viewModel.ExactDuplicateGroups.Should().ContainSingle();
        viewModel.SelectedExactDuplicateMembers.Should().HaveCount(2);
        viewModel.EpubAssessments.Should().HaveCount(2);
        viewModel.EpubAssessments.Should().OnlyContain(assessment => assessment.Status == "Disqualified");
        bookCollectionChanges.Should().Be(1);
        groupCollectionChanges.Should().Be(1);
    }

    private static MainWindowViewModel CreateViewModel(
        ILibraryFolderPicker picker,
        out ILibraryPathResolver resolver,
        out ICalibreMetadataReader reader,
        out IFormatFileHasher hasher,
        PersistedLibrarySnapshotsUseCase? persistedSnapshots = null,
        ILibraryStateSession? stateSession = null,
        IEbookViewerLauncher? ebookViewer = null,
        LibraryWorkflowOptions? workflowOptions = null,
        ICandidatePreparationWorkflow? candidatePreparation = null,
        IUnifiedCandidateCleanupExecutor? candidateExecutor = null,
        IUnifiedCandidateCleanupConfirmationService? candidateConfirmation = null)
    {
        resolver = A.Fake<ILibraryPathResolver>();
        reader = A.Fake<ICalibreMetadataReader>();
        IClock clock = A.Fake<IClock>();
        hasher = A.Fake<IFormatFileHasher>();
        return new(
            new ValidateLibraryUseCase(resolver),
            new ScanLibraryUseCase(resolver, reader, hasher, clock, new()),
            picker,
            ebookViewer: ebookViewer,
            persistedSnapshots: persistedSnapshots,
            libraryStateSession: stateSession,
            workflowOptions: workflowOptions,
            candidatePreparation: candidatePreparation,
            executeUnifiedCandidateCleanup: candidateExecutor,
            unifiedCandidateConfirmation: candidateConfirmation);
    }

    private static LibraryState WorkflowState(
        LibrarySnapshot snapshot,
        LibraryWorkflowPhase phase,
        LibraryWorkflowSource? source,
        LibraryStateGenerationId? generation = null)
    {
        LibraryStateGenerationId stateGeneration = generation ?? new(
            Guid.Parse("11111111-2222-3333-4444-555555555555"));
        return new(
            stateGeneration,
            new(0),
            LibraryStateStatus.Authoritative,
            snapshot,
            snapshot.ScannedAt,
            workflowCheckpoint: new(
                phase,
                stateGeneration,
                new(0),
                LibraryWorkflowPolicyVersions.Current,
                snapshot.ScannedAt,
                source));
    }

    private static LibrarySnapshot Snapshot(string libraryRoot) => new(
        new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, libraryRoot),
        new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
        [new(new(1), "Persisted Book", "Author", [new(new(1), "Author", "Author")], [], [], "Author/Persisted Book (1)")],
        []);

    private static LibrarySnapshot ExpandedSnapshot(string libraryRoot)
    {
        FormatFileFingerprint firstFingerprint = new(1_024, new(new string('a', 64)));
        FormatFileFingerprint secondFingerprint = new(2_048, new(new string('b', 64)));
        CalibreBook first = ExpandedBook(1, "First", "Author/First.epub", firstFingerprint);
        CalibreBook second = ExpandedBook(2, "Second", "Author/Second.epub", secondFingerprint, hasCover: true);
        WorkLanguageCandidateGroup group = WorkLanguageCandidateGroup.Create(
            "en",
            [first.Id, second.Id],
            [first.Id],
            WorkLanguageCandidateConfidence.Strong,
            [new("MATCH.CONTENT.AMBIGUOUS", CandidateEvidenceStrength.Supporting)],
            contentComparison: new(1, 0, 0, 1, 0, 0));
        return new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, libraryRoot),
            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
            [first, second],
            [],
            workLanguageCandidateGroups: [group],
            matchingRunSummary: new(
                MatchingPolicyVersion.V1,
                MatchingEvidenceStatus.Available,
                2,
                2,
                1,
                0,
                2,
                0,
                1,
                1,
                0));
    }

    private static LibrarySnapshot UnifiedSnapshot(string libraryRoot)
    {
        CalibreBook first = ExpandedBook(1, "First", "Author/First.epub", new(1_024, new(new string('a', 64))));
        CalibreBook second = ExpandedBook(
            2, "Second", "Author/Second.epub", new(2_048, new(new string('b', 64))), hasCover: true);
        WorkLanguageCandidateGroup expanded = WorkLanguageCandidateGroup.Create(
            "en",
            [first.Id, second.Id],
            [first.Id],
            WorkLanguageCandidateConfidence.Strong,
            [new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor)],
            contentComparison: new(1, 1, 0, 0, 0, 0));
        UnifiedCandidateGroup unified = UnifiedCandidateMergePolicy.Merge(
            [], [expanded], [first, second]).Single();
        return new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, libraryRoot),
            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
            [first, second],
            [],
            workLanguageCandidateGroups: [expanded],
            matchingRunSummary: new(
                MatchingPolicyVersion.Current,
                MatchingEvidenceStatus.Available,
                2,
                1,
                1,
                0,
                2,
                2,
                1,
                1,
                0),
            unifiedCandidateGroups: [unified]);
    }

    private static CalibreBook ExpandedBook(
        long id,
        string title,
        string relativePath,
        FormatFileFingerprint fingerprint,
        bool hasCover = false) => new(
        new(id),
        title,
        "Author",
        [new(new(id), "Author", "Author")],
        [],
        [new(
            "EPUB",
            title,
            relativePath,
            FormatFileStatus.Present,
            fingerprint,
            new(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))],
        "Author",
        new(languages: ["eng"], hasCover: hasCover));

    private static FormatHashResult Successful(int sequence, FormatFileFingerprint fingerprint) => FormatHashResult.Success(
        sequence,
        fingerprint,
        new FormatFileObservation(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

    private static CalibreCatalogRecord CreateCatalog(int bookCount = 1, bool secondBookHasPdf = false) => new(
        "87f7ed1f-59a8-45a6-975a-7e06fd84780d",
        27,
        Enumerable.Range(1, bookCount).Select(id =>
            new CalibreBookRecord(
                id,
                $"Book {id}",
                "Author",
                $"Book {id}",
                [new CalibreAuthorRecord(id, "Author", "Author")],
                [],
            id == 2 && secondBookHasPdf
                ? [new CalibreFormatRecord("EPUB", "Book"), new CalibreFormatRecord("PDF", "Book")]
                : [new CalibreFormatRecord("EPUB", "Book")])));

    private static CalibreCatalogRecord CreateMetadataDuplicateCatalog(
        string libraryUuid = "87f7ed1f-59a8-45a6-975a-7e06fd84780d") => new(
        libraryUuid,
        27,
        new[]
        {
            (Id: 1, Title: "Alpha : Book", Author: "Alice"),
            (Id: 2, Title: "alpha:book", Author: "Alice"),
            (Id: 3, Title: "Beta Book", Author: "Bob"),
            (Id: 4, Title: "BETA  BOOK", Author: "Bob"),
        }.Select(item => new CalibreBookRecord(
            item.Id,
            item.Title,
            $"{item.Author}, Sort",
            $"Book {item.Id}",
            [new CalibreAuthorRecord(item.Id, item.Author, $"{item.Author}, Sort")],
            [new CalibreIdentifierRecord("isbn", $"context-{item.Id}")],
            [new CalibreFormatRecord("EPUB", "Book")])));

    private static async Task<CalibreCatalogReadOutcome> WaitForCancellation(
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Cancellation was expected.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class SyntheticDuplicateLibrary : IDisposable
    {
        public SyntheticDuplicateLibrary()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"CalibreLibraryCleaner-Wpf-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
            string databasePath = Path.Combine(RootPath, "metadata.db");
            using SqliteConnection connection = new($"Data Source={databasePath};Mode=ReadWriteCreate;Pooling=False");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA user_version=27;
                CREATE TABLE library_id (id INTEGER PRIMARY KEY, uuid TEXT NOT NULL UNIQUE);
                CREATE TABLE books (id INTEGER PRIMARY KEY, title TEXT NOT NULL, author_sort TEXT, path TEXT NOT NULL, pubdate TIMESTAMP, series_index REAL NOT NULL DEFAULT 1.0, has_cover BOOL DEFAULT 0);
                CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT NOT NULL, sort TEXT);
                CREATE TABLE books_authors_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, author INTEGER NOT NULL, UNIQUE(book, author));
                CREATE TABLE identifiers (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, type TEXT NOT NULL COLLATE NOCASE, val TEXT NOT NULL COLLATE NOCASE, UNIQUE(book, type));
                CREATE TABLE data (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, format TEXT NOT NULL COLLATE NOCASE, name TEXT NOT NULL, UNIQUE(book, format));
                CREATE TABLE publishers (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE books_publishers_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, publisher INTEGER NOT NULL, UNIQUE(book));
                CREATE TABLE series (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE books_series_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, series INTEGER NOT NULL, UNIQUE(book));
                CREATE TABLE languages (id INTEGER PRIMARY KEY, lang_code TEXT NOT NULL);
                CREATE TABLE books_languages_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, lang_code INTEGER NOT NULL, item_order INTEGER NOT NULL DEFAULT 0, UNIQUE(book, lang_code));
                INSERT INTO library_id(id, uuid) VALUES (1, '87f7ed1f-59a8-45a6-975a-7e06fd84780d');
                INSERT INTO books(id, title, author_sort, path) VALUES (1, 'Book 1', 'Author 1', 'Author 1/Book (1)');
                INSERT INTO books(id, title, author_sort, path) VALUES (2, 'Book 2', 'Author 2', 'Author 2/Book (2)');
                INSERT INTO authors(id, name, sort) VALUES (1, 'Author 1', 'Author 1');
                INSERT INTO authors(id, name, sort) VALUES (2, 'Author 2', 'Author 2');
                INSERT INTO books_authors_link(id, book, author) VALUES (1, 1, 1);
                INSERT INTO books_authors_link(id, book, author) VALUES (2, 2, 2);
                INSERT INTO data(id, book, format, name) VALUES (1, 1, 'EPUB', 'Book 1');
                INSERT INTO data(id, book, format, name) VALUES (2, 2, 'EPUB', 'Book 2');
                """;
            command.ExecuteNonQuery();

            byte[] content = [1, 2, 3, 4];
            WriteFormat("Author 1", "Book (1)", "Book 1.epub", content);
            WriteFormat("Author 2", "Book (2)", "Book 2.epub", content);
        }

        public string RootPath { get; }

        public void Dispose() => Directory.Delete(RootPath, recursive: true);

        private void WriteFormat(string author, string book, string fileName, byte[] content)
        {
            string directory = Path.Combine(RootPath, author, book);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, fileName), content);
        }
    }
}
