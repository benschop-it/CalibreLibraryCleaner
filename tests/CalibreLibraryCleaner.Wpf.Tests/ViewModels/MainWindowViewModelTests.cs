using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.IO;
using System.Text.RegularExpressions;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindRepairsPublishesOnlySelectedRecordsAndReportsTheirScope(bool multiple)
    {
        const string libraryRoot = "C:\\Books";
        LibrarySnapshot template = Snapshot(libraryRoot);
        CalibreBook[] books = Enumerable.Range(1, 1000).Select(id => new CalibreBook(
            new(id), $"Generated Journey {id}", "Generated Writer",
            [new(new(id), "Generated Writer [12]", "Writer, Generated")], [],
            [new("EPUB", "book", $"Writer/Book ({id})/book.epub", FormatFileStatus.Present,
                new(10, new(new string('a', 64))),
                new(10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))], $"Writer/Book ({id})")).ToArray();
        LibrarySnapshot snapshot = new(template.Identity, template.ScannedAt, books, []);
        LibraryState completed = WorkflowState(snapshot, LibraryWorkflowPhase.Completed, new(new(Guid.NewGuid()), new(3)));
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        A.CallTo(() => store.ListAsync(A<CancellationToken>._)).Returns([new(libraryRoot, snapshot.ScannedAt)]);
        ILibraryStateSession session = A.Fake<ILibraryStateSession>();
        A.CallTo(() => session.LoadAsync(libraryRoot, A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Success(completed));
        A.CallTo(() => session.GetCurrent(libraryRoot)).Returns(completed);
        IUnifiedCandidateCleanupExecutor executor = A.Fake<IUnifiedCandidateCleanupExecutor>();
        A.CallTo(() => executor.ExecuteAsync(A<ExecuteUnifiedCandidateCleanupRequest>._,
                A<IProgress<UnifiedCandidateCleanupProgress>?>._, A<CancellationToken>._))
            .Returns(new UnifiedCandidateCleanupResult(new(Guid.NewGuid()),
                UnifiedCandidateCleanupState.Completed, 0, 0, 0, 0, [],
                VerifiedManagedNameBookIds: books.Select(value => value.Id).ToArray()));
        IMetadataMutationConfirmationService confirmation = A.Fake<IMetadataMutationConfirmationService>();
        A.CallTo(() => confirmation.ConfirmAuthorNormalization(A<int>._)).Returns(true);
        IQuarantineCleanupExecutor quarantine = A.Fake<IQuarantineCleanupExecutor>();
        IQuarantineCleanupConfirmationService quarantineConfirmation = A.Fake<IQuarantineCleanupConfirmationService>();
        IQuarantineDestinationInspector destination = A.Fake<IQuarantineDestinationInspector>();
        A.CallTo(() => destination.InspectAsync(
                libraryRoot, libraryRoot + " - Unresolvable", A<CancellationToken>._))
            .Returns(new QuarantineDestinationInspection(new(false, null)));
        List<long> requestedIds = [];
        PrepareMetadataReviewUseCase metadata = CreateMetadataReviewUseCase([],
            onProviderQuery: query => requestedIds.Add(query.BookId.Value));
        IFrontMatterAuthorCache frontMatterCache = A.Fake<IFrontMatterAuthorCache>();
        using MainWindowViewModel viewModel = CreateViewModel(A.Fake<ILibraryFolderPicker>(),
            out ILibraryPathResolver resolver, out _, out _, new(store), session,
            workflowOptions: LibraryWorkflowOptions.Staged, candidateExecutor: executor,
            metadataConfirmation: confirmation, enableFinalAuthorReview: true,
            quarantineDestination: destination, prepareMetadataReview: metadata,
            quarantineExecutor: quarantine, quarantineConfirmation: quarantineConfirmation,
            frontMatterCache: frontMatterCache);
        A.CallTo(() => resolver.ValidateAsync(libraryRoot, A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(new(libraryRoot, "database")));
        A.CallTo(() => resolver.ResolveFormat(A<ValidatedLibraryLocation>._,
                A<string>._, A<string>._, A<string>._))
            .Returns(ResolvedFormatPathOutcome.Failure("Generated unavailable inspection"));
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);
        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);
        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);
        Fake.ClearRecordedCalls(frontMatterCache);
        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);
        A.CallTo(() => frontMatterCache.TryReadAsync(A<FrontMatterAuthorCacheKey>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        requestedIds.Should().BeEmpty("showing quarantine choices must not run online identity repair");
        viewModel.SelectedQuarantineReviewSubject = null;
        viewModel.UpdateQuarantineReviewSelection([]);
        viewModel.ReviewIdentityRepairsCommand.CanExecute(null).Should().BeFalse();
        QuarantineReviewSubjectRowViewModel target = viewModel.QuarantineReviewSubjects.Single(value => value.RecordId == 467);
        viewModel.UpdateQuarantineReviewSelection(viewModel.QuarantineReviewSubjects.Where(
            value => value.RecordId <= 288 || value.RecordId == 467));
        viewModel.CheckSelectedQuarantineIncludeCommand.Execute(null);
        viewModel.UpdateQuarantineReviewSelection([viewModel.QuarantineReviewSubjects.Single(value => value.RecordId == 288)]);
        viewModel.UncheckSelectedQuarantineIncludeCommand.Execute(null);
        target = viewModel.QuarantineReviewSubjects.Single(value => value.RecordId == 467);
        QuarantineReviewSubjectRowViewModel other = viewModel.QuarantineReviewSubjects.Single(value => value.RecordId == 999);
        other.Include = true;
        await other.PersistIncludeCommand.ExecuteAsync(null);
        viewModel.SelectedQuarantineReviewSubject = target;
        if (multiple)
            viewModel.UpdateQuarantineReviewSelection(viewModel.QuarantineReviewSubjects.Where(
                value => value.RecordId == 467 || value.RecordId >= 901 && value.RecordId <= 906));
        else
            viewModel.UpdateQuarantineReviewSelection([]);
        long[] expected = multiple ? [467, 901, 902, 903, 904, 905, 906] : [467];
        long[] checkedBeforeRepair = viewModel.QuarantineReviewSubjects.Where(value => value.Include)
            .Select(value => value.RecordId).ToArray();

        viewModel.ReviewIdentityRepairsCommand.CanExecute(null).Should().BeTrue();
        await viewModel.ReviewIdentityRepairsCommand.ExecuteAsync(null);

        requestedIds.Should().NotBeEmpty().And.OnlyContain(id => expected.Contains(id));
        requestedIds.Distinct().Should().BeEquivalentTo(expected);
        viewModel.MetadataReviewSubjects.Select(value => value.TargetBookId).Should().BeEquivalentTo(expected);
        viewModel.MetadataReviewSubjects.Should().OnlyContain(value => !value.Apply);
        viewModel.StatusMessage.Should().Contain(multiple ? "7 selected records only" : "record ID 467 only");
        viewModel.QuarantineReviewSubjects.Single(value => value.RecordId == 999).Include.Should().BeTrue();
        viewModel.QuarantineReviewSubjects.Where(value => value.Include).Select(value => value.RecordId)
            .Should().Equal(checkedBeforeRepair, "finding repairs must preserve every move decision");
        await viewModel.CheckAllMetadataApplyCommand.ExecuteAsync(null);
        viewModel.ShowAllQuarantineRecords = true;
        viewModel.ShowAllQuarantineRecords = false;
        viewModel.QuarantineReviewSubjects.Where(value => value.Include).Select(value => value.RecordId)
            .Should().Equal(checkedBeforeRepair, "checking a metadata proposal or changing the view must not clear move choices");
        viewModel.QuarantineReviewSubjects.Single(value => value.RecordId == 288).Include.Should().BeFalse();
        checkedBeforeRepair.Should().HaveCount(289);
        viewModel.CandidateCleanupAutomationName.Should().Be("Move checked records to quarantine",
            "metadata repair choices must not hijack the quarantine action");
        viewModel.QuarantineMoveActionText.Should().StartWith("Move 289 checked");
        viewModel.IdentityRepairActionText.Should().StartWith($"Apply {expected.Length} checked metadata");
        viewModel.ApplyCheckedIdentityMetadataCommand.CanExecute(null).Should().BeTrue();
        await viewModel.ApplyCheckedIdentityMetadataCommand.ExecuteAsync(null);
        A.CallTo(() => confirmation.ConfirmExternalBackup(expected.Length)).MustHaveHappenedOnceExactly();
        A.CallTo(() => quarantineConfirmation.ConfirmMove(A<int>._, A<string>._, A<bool>._))
            .MustNotHaveHappened();
        viewModel.MoveCheckedQuarantineCommand.CanExecute(null).Should().BeTrue();
        await viewModel.MoveCheckedQuarantineCommand.ExecuteAsync(null);
        A.CallTo(() => quarantineConfirmation.ConfirmMove(289, libraryRoot + " - Unresolvable", false))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => confirmation.ConfirmExternalBackup(expected.Length)).MustHaveHappenedOnceExactly();
        viewModel.QuarantineReviewSubjects.Where(value => value.Include).Select(value => value.RecordId)
            .Should().Equal(checkedBeforeRepair);
        A.CallTo(() => quarantine.ExecuteAsync(A<ExecuteQuarantineCleanupRequest>._,
            A<IProgress<QuarantineCleanupProgress>?>._, A<CancellationToken>._)).MustNotHaveHappened();
        session.GetCurrent(libraryRoot).Should().BeSameAs(completed);
        viewModel.IsBusy.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuarantineCheckAllShownChecksNeedsReviewProblemsAndNeverHiddenBooks(bool noProblems)
    {
        const string libraryRoot = "C:\\Books";
        LibrarySnapshot template = Snapshot(libraryRoot);
        CalibreBook Book(long id, string title, string author) => new(new(id), title, author,
            [new(new(id), author, author)], [],
            [new("EPUB", "book", $"Writer/Book ({id})/book.epub", FormatFileStatus.Present,
                new(10, new(new string('a', 64))),
                new(10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))], $"Writer/Book ({id})");
        CalibreBook[] books = noProblems
            ? [Book(1, "A Good Book", "Reliable Writer")]
            : [Book(1, "A Good Book", "Reliable Writer"), Book(2, "Another Book", "Another Book"),
                Book(3, "The Journey", "Other Writer [12]")];
        LibrarySnapshot snapshot = new(template.Identity, template.ScannedAt, books, []);
        LibraryState completed = WorkflowState(snapshot, LibraryWorkflowPhase.Completed,
            new(new(Guid.NewGuid()), new(3)));
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        A.CallTo(() => store.ListAsync(A<CancellationToken>._)).Returns([new(libraryRoot, snapshot.ScannedAt)]);
        ILibraryStateSession session = A.Fake<ILibraryStateSession>();
        A.CallTo(() => session.LoadAsync(libraryRoot, A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Success(completed));
        A.CallTo(() => session.GetCurrent(libraryRoot)).Returns(completed);
        IUnifiedCandidateCleanupExecutor executor = A.Fake<IUnifiedCandidateCleanupExecutor>();
        A.CallTo(() => executor.ExecuteAsync(A<ExecuteUnifiedCandidateCleanupRequest>._,
                A<IProgress<UnifiedCandidateCleanupProgress>?>._, A<CancellationToken>._))
            .Returns(new UnifiedCandidateCleanupResult(new(Guid.NewGuid()),
                UnifiedCandidateCleanupState.Completed, 0, 0, 0, 0, [],
                VerifiedManagedNameBookIds: books.Select(value => value.Id).ToArray()));
        IMetadataMutationConfirmationService confirmation = A.Fake<IMetadataMutationConfirmationService>();
        A.CallTo(() => confirmation.ConfirmAuthorNormalization(A<int>._)).Returns(true);
        IQuarantineDestinationInspector destination = A.Fake<IQuarantineDestinationInspector>();
        A.CallTo(() => destination.InspectAsync(
                libraryRoot, libraryRoot + " - Unresolvable", A<CancellationToken>._))
            .Returns(new QuarantineDestinationInspection(new(false, null)));
        IQuarantineCleanupExecutor quarantine = A.Fake<IQuarantineCleanupExecutor>();
        LibraryOperationCoordinator coordinator = new();
        using MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out ILibraryPathResolver resolver, out _, out _, new(store), session,
            workflowOptions: LibraryWorkflowOptions.Staged, candidateExecutor: executor,
            operationCoordinator: coordinator, metadataConfirmation: confirmation, enableFinalAuthorReview: true,
            quarantineDestination: destination, quarantineExecutor: quarantine);
        A.CallTo(() => resolver.ValidateAsync(libraryRoot, A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(new(libraryRoot, "database")));
        A.CallTo(() => resolver.ResolveFormat(A<ValidatedLibraryLocation>._,
                A<string>._, A<string>._, A<string>._))
            .Returns(ResolvedFormatPathOutcome.Failure("Generated unavailable inspection"));
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);
        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);
        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);
        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);

        viewModel.ShowAllQuarantineRecords.Should().BeFalse();
        viewModel.QuarantineReviewSubjects.Select(value => value.RecordId)
            .Should().Equal(noProblems ? [] : new long[] { 2, 3 });
        viewModel.QuarantineReviewSubjects.Should().OnlyContain(value => !value.Include);
        viewModel.QuarantineVerificationSummary.Should().Contain($"Retained input {books.Length}");
        viewModel.CheckAllQuarantineIncludeCommand.CanExecute(null).Should().Be(!noProblems);
        if (noProblems)
        {
            viewModel.ShowAllQuarantineRecords = true;
            QuarantineReviewSubjectRowViewModel normal = viewModel.QuarantineReviewSubjects.Should().ContainSingle().Subject;
            normal.ProblemSummary.Should().Contain("No specific");
            normal.SuggestedAction.Should().Contain("Missing verification");
            await viewModel.CandidateCleanupCommand.ExecuteAsync(null);
            viewModel.StatusMessage.Should().Contain("nothing will be moved").And.Contain("not fully verified");
            A.CallTo(() => quarantine.ExecuteAsync(A<ExecuteQuarantineCleanupRequest>._,
                A<IProgress<QuarantineCleanupProgress>?>._, A<CancellationToken>._)).MustNotHaveHappened();
            return;
        }

        using (coordinator.TryBegin())
            viewModel.CheckAllQuarantineIncludeCommand.CanExecute(null).Should().BeFalse();
        viewModel.CheckAllQuarantineIncludeCommand.Execute(null);

        viewModel.QuarantineReviewSubjects.Should().OnlyContain(value => value.Include && value.IsManual);
        viewModel.StatusMessage.Should().Contain("Checked 2 shown").And.Contain("No files have been moved");
        viewModel.CheckAllQuarantineIncludeCommand.CanExecute(null).Should().BeFalse();
        viewModel.ShowAllQuarantineRecords = true;
        viewModel.QuarantineReviewSubjects.Should().HaveCount(3);
        viewModel.QuarantineReviewSubjects.Single(value => value.RecordId == 1).Include.Should().BeFalse();
        viewModel.ClearAllQuarantineIncludeCommand.Execute(null);
        QuarantineReviewSubjectRowViewModel row = viewModel.QuarantineReviewSubjects.Single(value => value.RecordId == 2);
        row.Include = true;
        await row.PersistIncludeCommand.ExecuteAsync(null);
        row = viewModel.QuarantineReviewSubjects.Single(value => value.RecordId == 3);
        viewModel.UpdateQuarantineReviewSelection([row]);
        viewModel.CheckSelectedQuarantineIncludeCommand.Execute(null);
        viewModel.ShowAllQuarantineRecords = false;
        viewModel.QuarantineReviewSubjects.Should().OnlyContain(value => value.Include);
        viewModel.QuarantineReviewSubjects.Select(value => value.RecordId).Should().Equal(2, 3);
        A.CallTo(() => quarantine.ExecuteAsync(A<ExecuteQuarantineCleanupRequest>._,
            A<IProgress<QuarantineCleanupProgress>?>._, A<CancellationToken>._)).MustNotHaveHappened();
        session.GetCurrent(libraryRoot).Should().BeSameAs(completed);
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("load")]
    [InlineData("load-replaced")]
    [InlineData("candidate")]
    [InlineData("metadata")]
    public async Task ReadOnlyRegexTimeoutIsReportedWithoutFaultingTheCommandOrReplacingResults(string action)
    {
        const string libraryRoot = "C:\\Books";
        LibrarySnapshot snapshot = Snapshot(libraryRoot);
        LibraryWorkflowSource source = new(new(Guid.NewGuid()), new(3));
        LibraryState state = WorkflowState(snapshot, action == "metadata"
            ? LibraryWorkflowPhase.CandidateCleanupCompleted : LibraryWorkflowPhase.CandidatePreparationReady, source);
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        A.CallTo(() => store.ListAsync(A<CancellationToken>._)).Returns([new(libraryRoot, snapshot.ScannedAt)]);
        ILibraryStateSession session = A.Fake<ILibraryStateSession>();
        A.CallTo(() => session.LoadAsync(libraryRoot, A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Success(state));
        LibraryState current = state;
        A.CallTo(() => session.GetCurrent(libraryRoot)).ReturnsLazily(() => current);
        RegexMatchTimeoutException timeout = new(
            "PRIVATE_REGEX_INPUT", "PRIVATE_REGEX_PATTERN", TimeSpan.FromMilliseconds(50));
        ICandidatePreparationWorkflow preparation = A.Fake<ICandidatePreparationWorkflow>();
        A.CallTo(() => preparation.AnalyzeAsync(
                libraryRoot, A<IProgress<CandidatePreparationProgress>?>._, A<CancellationToken>._))
            .Throws(timeout);
        bool failMetadata = true;
        PrepareMetadataReviewUseCase metadata = CreateMetadataReviewUseCase([], () =>
        {
            if (failMetadata) throw timeout;
        });
        LibraryOperationCoordinator coordinator = new();
        CapturingLogger<MainWindowViewModel> logger = new();
        using MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader, out _, new(store), session,
            workflowOptions: LibraryWorkflowOptions.Staged, candidatePreparation: preparation,
            operationCoordinator: coordinator, prepareMetadataReview: metadata, logger: logger);
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);
        BookRowViewModel[] previousRows = viewModel.Books.ToArray();
        if (action == "scan")
        {
            ValidatedLibraryLocation location = new(libraryRoot, "database");
            A.CallTo(() => resolver.ValidateAsync(libraryRoot, A<CancellationToken>._))
                .Returns(LibraryValidationOutcome.Success(location));
            A.CallTo(() => reader.ReadAsync(
                    location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
                .Throws(timeout);
        }
        else if (action == "load")
            A.CallTo(() => session.LoadAsync(libraryRoot, A<CancellationToken>._)).Throws(timeout);
        else if (action == "load-replaced")
            A.CallTo(() => session.LoadAsync(libraryRoot, A<CancellationToken>._)).ReturnsLazily(() =>
            {
                current = WorkflowState(Snapshot(libraryRoot), LibraryWorkflowPhase.CandidatePreparationReady, source);
                return Task.FromException<LibraryStateSessionOutcome>(timeout);
            });

        CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command = action switch
        {
            "scan" => viewModel.ScanCommand,
            "load" or "load-replaced" => viewModel.LoadPersistedSnapshotCommand,
            _ => viewModel.CandidateCleanupCommand,
        };
        command.Execute(null);
        Func<Task> execute = () => command.ExecutionTask!;
        await execute.Should().NotThrowAsync();

        command.ExecutionTask!.IsCompletedSuccessfully.Should().BeTrue();
        viewModel.ErrorMessage.Should().Contain("ANALYSIS.REGEX_TIMEOUT");
        viewModel.ErrorAction.Should().NotBeNullOrWhiteSpace();
        viewModel.StatusMessage.Should().Contain("stopped");
        viewModel.Books.Should().Equal(previousRows);
        viewModel.IsBusy.Should().BeFalse();
        viewModel.IsProgressIndeterminate.Should().BeFalse();
        coordinator.IsOperationActive.Should().BeFalse();
        viewModel.ScanCommand.CanExecute(null).Should().BeTrue();
        if (action == "load-replaced")
        {
            viewModel.CandidateCleanupCommand.CanExecute(null).Should().BeFalse();
            viewModel.ErrorAction.Should().Contain("Reload");
            A.CallTo(() => session.LoadAsync(libraryRoot, A<CancellationToken>._))
                .Returns(LibraryStateSessionOutcome.Success(current));
            await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);
            viewModel.CandidateCleanupCommand.CanExecute(null).Should().BeTrue();
        }
        else
            session.GetCurrent(libraryRoot).Should().BeSameAs(state);
        A.CallTo(() => session.MarkUncertainAsync(
            A<string>._, A<LibraryStateUncertainty>._, A<CancellationToken>._)).MustNotHaveHappened();
        logger.Entries.Should().Contain(value => value.Level == LogLevel.Error);
        logger.Entries.Should().OnlyContain(value =>
            !value.Message.Contains("PRIVATE_REGEX_INPUT", StringComparison.Ordinal)
            && !value.Message.Contains("PRIVATE_REGEX_PATTERN", StringComparison.Ordinal));

        if (action == "metadata")
        {
            failMetadata = false;
            await viewModel.CandidateCleanupCommand.ExecuteAsync(null);
            viewModel.ErrorMessage.Should().BeEmpty();
            viewModel.StatusMessage.Should().Contain("metadata review ready");
            viewModel.MetadataReviewFilterMode = MetadataReviewFilterMode.All;
            viewModel.MetadataReviewSubjects.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void OperationGateReleaseRefreshesDispositionCommandsForActualWpfBindings()
    {
        LibraryOperationCoordinator coordinator = new();
        using MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, operationCoordinator: coordinator);
        int repairChanges = 0;
        int acceptanceChanges = 0;
        viewModel.ReviewIdentityRepairsCommand.CanExecuteChanged += (_, _) => repairChanges++;
        viewModel.AcceptSelectedQualificationCommand.CanExecuteChanged += (_, _) => acceptanceChanges++;

        using (coordinator.TryBegin())
        {
            viewModel.IsOperationActive.Should().BeTrue();
        }

        viewModel.IsOperationActive.Should().BeFalse();
        repairChanges.Should().Be(2, "WPF must be notified when the operation starts and when its gate is released");
        acceptanceChanges.Should().Be(2);
    }

    [Theory]
    [InlineData("Library", true)]
    [InlineData("Library", false)]
    [InlineData("Metadata", true)]
    [InlineData("Metadata", false)]
    [InlineData("FinalAuthors", true)]
    [InlineData("FinalAuthors", false)]
    public async Task BookRowsPreferPresentEpubThenPdf(string rowKind, bool epubPresent)
    {
        BookFormat[] formats =
        [
            ViewerFormat("MOBI"),
            ViewerFormat("PDF"),
            ViewerFormat("EPUB", epubPresent ? FormatFileStatus.Present : FormatFileStatus.Missing),
        ];
        (MainWindowViewModel viewModel, IEbookViewerLauncher viewer) = await LoadViewerSnapshotAsync(formats);
        using (viewModel)
        {
            object row = rowKind switch
            {
                "Metadata" => new MetadataReviewSubjectRowViewModel(
                    new(new(new("metadata-review/book/1"), null, [new(1)], new(1),
                        new(FusedEditionMetadataConfidence.Unavailable, null, null, [], [],
                            ["METADATA.FUSION.CONFIDENCE_UNAVAILABLE"])),
                        false, false, null, false),
                    new(new(1), "Book", "Author", [], [], formats, "Author"), (_, _) => Task.CompletedTask),
                "FinalAuthors" => new FinalAuthorReviewSubjectRowViewModel(
                    new(new(1), "Book", [], [],
                        new(AuthorResolutionStatus.Unresolved, CurrentAuthorProtection.Unverified,
                            [], [], AuthorProposalSource.None, [], selectedByDefault: false),
                        FinalAuthorEvidenceCoverage.None, false, [], false),
                    (_, _) => Task.CompletedTask),
                _ => viewModel.Books[0],
            };
            string expectedFormat = epubPresent ? "EPUB" : "PDF";
            viewModel.OpenBookRowCommand.CanExecute(row).Should().BeTrue();

            await viewModel.OpenBookRowCommand.ExecuteAsync(row);

            A.CallTo(() => viewer.LaunchAsync(
                A<EbookViewerLaunchRequest>.That.Matches(request => request.LibraryRoot == "C:\\Books"
                    && request.ExpectedRelativePath == $"Author/Book.{expectedFormat.ToLowerInvariant()}"),
                A<CancellationToken>._)).MustHaveHappenedOnceExactly();
            viewModel.StatusMessage.Should().Contain(epubPresent ? "Calibre ebook viewer" : "default PDF viewer");
            if (row is MetadataReviewSubjectRowViewModel metadata) metadata.Apply.Should().BeFalse();
            if (row is FinalAuthorReviewSubjectRowViewModel authors) authors.Apply.Should().BeFalse();
        }
    }

    [Fact]
    public async Task BookWithoutPresentEpubOrPdfReportsUnavailableWithoutLaunching()
    {
        (MainWindowViewModel viewModel, IEbookViewerLauncher viewer) = await LoadViewerSnapshotAsync(
        [
            ViewerFormat("MOBI"),
            ViewerFormat("EPUB", FormatFileStatus.Missing),
            ViewerFormat("PDF", FormatFileStatus.Inaccessible),
        ]);
        using (viewModel)
        {
            await viewModel.OpenBookRowCommand.ExecuteAsync(viewModel.Books[0]);

            viewModel.ErrorMessage.Should().Contain("no present EPUB or PDF");
            A.CallTo(() => viewer.LaunchAsync(A<EbookViewerLaunchRequest>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }
    }

    [Fact]
    public async Task ExactFormatAndAssessmentRowsOpenTheirOwnPathRatherThanPreferredBookFormat()
    {
        (MainWindowViewModel viewModel, IEbookViewerLauncher viewer) = await LoadViewerSnapshotAsync(
            [ViewerFormat("EPUB")]);
        using (viewModel)
        {
            FormatRowViewModel format = new(ViewerFormat("PDF", relativePath: "Author/Other.pdf"));
            EpubAssessmentRowViewModel epub = new(new(
                new(1), "EPUB", "Author/Assessed.epub", null, AssessmentStatus.Unassessed, null,
                new("epub-inspector/1.0.0"), new("epub-quality/1.0.0"), new(false, false),
                [new("EPUB.PACKAGE", FindingSeverity.Warning, 0, "Package unavailable.")]), null);
            PdfAssessmentRowViewModel pdf = new(
                new PdfAssessmentEngine(new PdfClassificationPolicy()).Assess(
                    new(1), "Author/Assessed.pdf", null,
                    PdfInspectionResult.Failed(new(1), "Author/Assessed.pdf", PdfInspectionProblemCode.PasswordRequired,
                        PdfOpenStatus.Unreadable, PdfEncryptionStatus.PasswordRequired)), null);

            foreach (object row in new object[] { format, epub, pdf })
            {
                viewModel.OpenBookRowCommand.CanExecute(row).Should().BeTrue();
                await viewModel.OpenBookRowCommand.ExecuteAsync(row);
            }

            Fake.GetCalls(viewer).Select(call => ((EbookViewerLaunchRequest)call.Arguments[0]!).ExpectedRelativePath)
                .Should().Equal("Author/Other.pdf", "Author/Assessed.epub", "Author/Assessed.pdf");
            viewModel.StatusMessage.Should().Contain("default PDF viewer");
        }
    }

    [Fact]
    public void OpenBookRowIsDisabledDuringSharedOperationsAndForNonBookRows()
    {
        LibraryOperationCoordinator coordinator = new();
        using MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _,
            ebookViewer: A.Fake<IEbookViewerLauncher>(), operationCoordinator: coordinator);
        FormatRowViewModel row = new(ViewerFormat("PDF"));
        int notifications = 0;
        viewModel.OpenBookRowCommand.CanExecuteChanged += (_, _) => notifications++;

        viewModel.OpenBookRowCommand.CanExecute(row).Should().BeTrue();
        viewModel.OpenBookRowCommand.CanExecute(null).Should().BeFalse();
        viewModel.OpenBookRowCommand.CanExecute(new object()).Should().BeFalse();
        using (coordinator.TryBegin())
            viewModel.OpenBookRowCommand.CanExecute(row).Should().BeFalse();
        viewModel.OpenBookRowCommand.CanExecute(row).Should().BeTrue();
        notifications.Should().Be(2);
    }

    [Fact]
    public void OperationProgressIncludesCompletedAndTotalCounts()
    {
        MainWindowViewModel.FormatOperationProgress("Processing metadata", 400, 20_206)
            .Should().Be("Processing metadata (400 of 20,206).");
    }

    [Fact]
    public void MetadataResultSeparatesUpdatedAndVerifiedUnchangedCounts()
    {
        UnifiedCandidateCleanupResult result = new(
            new(Guid.Parse("99999999-8888-7777-6666-555555555555")),
            UnifiedCandidateCleanupState.Completed,
            0, 0, 0, 0, [],
            UpdatedMetadataFieldCount: 123,
            SkippedMetadataFieldCount: 7,
            OmittedMetadataCoverCount: 2,
            OmittedMetadataAuthorFieldCount: 3);

        MainWindowViewModel.FormatMetadataResultSummary(result)
            .Should().Be("Updated 123 metadata field(s); skipped 7 verified-unchanged field(s); omitted 2 transiently unavailable cover(s); omitted 3 ambiguous author field(s).");
    }

    [Fact]
    public void OnlineMetadataSettingsOwnsSharedOperationLease()
    {
        LibraryOperationCoordinator operationCoordinator = new();
        IOnlineMetadataSettingsDialogService dialog = A.Fake<IOnlineMetadataSettingsDialogService>();
        A.CallTo(() => dialog.Show()).Invokes(() =>
        {
            operationCoordinator.IsOperationActive.Should().BeTrue();
            operationCoordinator.TryBegin().Should().BeNull();
        });
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(),
            out _,
            out _,
            out _,
            operationCoordinator: operationCoordinator,
            onlineMetadataSettingsDialog: dialog);

        viewModel.OnlineMetadataSettingsCommand.Execute(null);

        A.CallTo(() => dialog.Show()).MustHaveHappenedOnceExactly();
        operationCoordinator.IsOperationActive.Should().BeFalse();
    }

    [Fact]
    public async Task StagedScanWithoutExactGroupsMakesCandidatePreparationAvailable()
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
                    .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, snapshot.ScannedAt)
                    .AdvanceWorkflow(LibraryWorkflowPhase.CandidatePreparationReady, snapshot.ScannedAt);
                return Task.FromResult(LibraryStateSessionOutcome.Success(current));
            });
        A.CallTo(() => stateSession.GetCurrent(A<string>.That.IsNotNull())).ReturnsLazily(() => current);
        MainWindowViewModel viewModel = CreateViewModel(
            picker,
            out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader,
            out IFormatFileHasher hasher,
            stateSession: stateSession,
            workflowOptions: LibraryWorkflowOptions.Staged,
            candidatePreparation: A.Fake<ICandidatePreparationWorkflow>());
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

        current!.WorkflowCheckpoint.Phase.Should().Be(LibraryWorkflowPhase.CandidatePreparationReady);
        viewModel.StatusMessage.Should().StartWith("Exact-only analysis complete");
        viewModel.CandidateCleanupCommand.CanExecute(null).Should().BeTrue();
        viewModel.CandidateCleanupAutomationName.Should().Be("Prepare Candidate review");
        viewModel.ExpandedCandidateSummary.Should().Contain("Prepare Candidate review")
            .And.NotContain("Run a fresh scan");
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
    public async Task FirstCandidateActivationPreparesOnlyUnifiedReviewAndNeverMutates()
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
        List<MetadataReviewDecision> metadataDecisions = [];
        int metadataProviderRequests = 0;
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), stateSession,
            workflowOptions: LibraryWorkflowOptions.Staged,
            candidatePreparation: preparation,
            candidateExecutor: executor,
            candidateConfirmation: confirmation,
            prepareMetadataReview: CreateMetadataReviewUseCase(
                metadataDecisions, () => metadataProviderRequests++));
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
        viewModel.MetadataReviewSubjects.Should().BeEmpty();
        viewModel.MetadataReviewSummary.Should().Contain("Complete Candidate cleanup");
        metadataProviderRequests.Should().Be(0);
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
                    0,
                    100,
                    CandidateProgressUnit.Files,
                    "Preparing EPUB assessment workload"));
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

        viewModel.IsProgressIndeterminate.Should().BeTrue();
        viewModel.ProgressPercentage.Should().Be(0);
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
        List<MetadataReviewDecision> metadataDecisions = [];
        int metadataProviderRequests = 0;
        CapturingLogger<MainWindowViewModel> logger = new();
        ExecuteUnifiedCandidateCleanupRequest? captured = null;
        A.CallTo(() => executor.ExecuteAsync(
                A<ExecuteUnifiedCandidateCleanupRequest>._,
                A<IProgress<UnifiedCandidateCleanupProgress>?>._,
                A<CancellationToken>._))
            .Invokes(call => captured = call.GetArgument<ExecuteUnifiedCandidateCleanupRequest>(0))
            .ReturnsLazily(() =>
            {
                current = analyzed.AdvanceWorkflow(
                    LibraryWorkflowPhase.CandidateCleanupCompleted, analyzed.ProjectedAtUtc);
                return Task.FromResult(new UnifiedCandidateCleanupResult(
                    new(Guid.Parse("99999999-8888-7777-6666-555555555555")),
                    UnifiedCandidateCleanupState.Completed,
                    0,
                    1,
                    1,
                    0,
                    []));
            });
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), stateSession,
            workflowOptions: LibraryWorkflowOptions.Staged,
            candidatePreparation: preparation,
            candidateExecutor: executor,
            candidateConfirmation: confirmation,
            prepareMetadataReview: CreateMetadataReviewUseCase(
                metadataDecisions, () => metadataProviderRequests++),
            logger: logger);
        List<bool> bulkSkipStates = [];
        viewModel.SkipAllUnifiedCandidatesCommand.CanExecuteChanged += (_, _) =>
            bulkSkipStates.Add(viewModel.SkipAllUnifiedCandidatesCommand.CanExecute(null));
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);
        bulkSkipStates.Should().Contain(true);
        UnifiedCandidateGroupRowViewModel group = viewModel.UnifiedCandidateGroups.Single();
        viewModel.SkipAllUnifiedCandidatesCommand.CanExecute(null).Should().BeTrue();
        viewModel.SkipAllUnifiedCandidatesCommand.Execute(null);
        group.Skip.Should().BeTrue();
        viewModel.StatusMessage.Should().Be("Marked 1 Candidate group(s) to Skip.");
        group.Skip = false;
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
        captured.MetadataReview.Should().BeNull();
        captured.GroupSelections.Should().ContainSingle(value =>
            value.KeeperBookId == new CalibreBookId(1)
            && value.KeeperWasOverridden
            && !value.Skip);
        viewModel.CandidateCleanupCommand.CanExecute(null).Should().BeTrue();
        viewModel.CandidateCleanupAutomationName.Should().Be("Prepare online metadata review");
        viewModel.CandidateCleanupResultSummary.Should().Contain("removed 1 format");
        logger.Entries.Should().Contain(value =>
            value.EventId.Name == "UserVisibleOperationResult"
            && value.Message == viewModel.CandidateCleanupResultSummary);

        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);

        viewModel.UnifiedCandidateGroups.Should().ContainSingle();
        metadataProviderRequests.Should().BeGreaterThan(0);
        viewModel.ErrorMessage.Should().BeEmpty();
        viewModel.StatusMessage.Should().StartWith("Online metadata review ready");
        viewModel.IsProgressIndeterminate.Should().BeFalse();
        viewModel.ProgressPercentage.Should().Be(100);
        logger.Entries.Should().Contain(value =>
            value.EventId.Name == "UserVisibleStatus"
            && value.Message == viewModel.StatusMessage);
        logger.Entries.Should().Contain(value =>
            value.EventId.Name == "UserVisibleMetadataReviewSummary"
            && value.Message == viewModel.MetadataReviewSummary);
        UnifiedCandidateMemberRowViewModel staleAlternate = viewModel.SelectedUnifiedCandidateGroup!.Members
            .Single(value => value.BookId == 2);
        Action selectStaleCandidateMember = () => viewModel.SelectedUnifiedCandidateMember = staleAlternate;
        selectStaleCandidateMember.Should().NotThrow();
        A.CallTo(() => executor.ExecuteAsync(
                A<ExecuteUnifiedCandidateCleanupRequest>._,
                A<IProgress<UnifiedCandidateCleanupProgress>?>._,
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task MetadataApplyOverrideRestoresAfterExplicitPostCleanupPreparation()
    {
        const string libraryRoot = "C:\\Books";
        LibrarySnapshot snapshot = Snapshot(libraryRoot);
        LibraryWorkflowSource source = new(
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")), new(3));
        LibraryState analyzed = WorkflowState(snapshot, LibraryWorkflowPhase.CandidateCleanupCompleted, source);
        ILibrarySnapshotStore snapshotStore = A.Fake<ILibrarySnapshotStore>();
        A.CallTo(() => snapshotStore.ListAsync(A<CancellationToken>._))
            .Returns([new(libraryRoot, snapshot.ScannedAt)]);
        ILibraryStateSession stateSession = A.Fake<ILibraryStateSession>();
        A.CallTo(() => stateSession.LoadAsync(libraryRoot, A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Success(analyzed));
        A.CallTo(() => stateSession.GetCurrent(libraryRoot)).Returns(analyzed);
        List<MetadataReviewDecision> persisted = [];
        int metadataProviderRequests = 0;
        PrepareMetadataReviewUseCase metadataReview = CreateMetadataReviewUseCase(
            persisted, () => metadataProviderRequests++);
        MainWindowViewModel first = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(snapshotStore), stateSession,
            workflowOptions: LibraryWorkflowOptions.Staged,
            prepareMetadataReview: metadataReview);
        await first.InitializeAsync();
        first.SelectedPersistedLibraryPath = libraryRoot;
        await first.LoadPersistedSnapshotCommand.ExecuteAsync(null);
        first.MetadataReviewSubjects.Should().BeEmpty();
        metadataProviderRequests.Should().Be(0);
        await first.CandidateCleanupCommand.ExecuteAsync(null);
        metadataProviderRequests.Should().BeGreaterThan(0);
        first.MetadataReviewFilterMode = MetadataReviewFilterMode.All;
        MetadataReviewSubjectRowViewModel row = first.MetadataReviewSubjects.Should().ContainSingle().Subject;
        first.SelectedMetadataReviewSubject.Should().BeSameAs(row);
        string proposedEdition = row.Provenance;

        first.ClearAllMetadataApplyCommand.CanExecute(null).Should().BeTrue();
        await first.ClearAllMetadataApplyCommand.ExecuteAsync(null);
        first.ClearAllMetadataApplyCommand.CanExecute(null).Should().BeFalse();
        row = first.MetadataReviewSubjects.Should().ContainSingle().Subject;
        row.Apply.Should().BeFalse();
        row.IsOverride.Should().BeTrue();
        first.SelectedMetadataReviewSubject.Should().BeSameAs(row);
        row.Provenance.Should().Be(proposedEdition);

        first.CheckAllMetadataApplyCommand.CanExecute(null).Should().BeTrue();
        await first.CheckAllMetadataApplyCommand.ExecuteAsync(null);
        row = first.MetadataReviewSubjects.Should().ContainSingle().Subject;
        row.Apply.Should().BeTrue();
        first.UpdateMetadataReviewSelection([row]);
        first.UncheckSelectedMetadataApplyCommand.CanExecute(null).Should().BeTrue();
        await first.UncheckSelectedMetadataApplyCommand.ExecuteAsync(null);
        row = first.MetadataReviewSubjects.Should().ContainSingle().Subject;
        row.Apply.Should().BeFalse();
        first.UpdateMetadataReviewSelection([row]);
        first.CheckSelectedMetadataApplyCommand.CanExecute(null).Should().BeTrue();
        await first.CheckSelectedMetadataApplyCommand.ExecuteAsync(null);
        first.MetadataReviewSubjects.Should().ContainSingle().Which.Apply.Should().BeTrue();
        await first.ClearAllMetadataApplyCommand.ExecuteAsync(null);
        persisted.Should().ContainSingle(value => !value.Apply);

        MainWindowViewModel restarted = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(snapshotStore), stateSession,
            workflowOptions: LibraryWorkflowOptions.Staged,
            prepareMetadataReview: metadataReview);
        await restarted.InitializeAsync();
        restarted.SelectedPersistedLibraryPath = libraryRoot;
        await restarted.LoadPersistedSnapshotCommand.ExecuteAsync(null);
        restarted.MetadataReviewSubjects.Should().BeEmpty();
        int requestsBeforeExplicitPreparation = metadataProviderRequests;
        await restarted.CandidateCleanupCommand.ExecuteAsync(null);
        metadataProviderRequests.Should().BeGreaterThan(requestsBeforeExplicitPreparation);
        restarted.MetadataReviewFilterMode = MetadataReviewFilterMode.All;
        MetadataReviewSubjectRowViewModel restored = restarted.MetadataReviewSubjects.Should().ContainSingle().Subject;

        restored.Apply.Should().BeFalse();
        restored.IsOverride.Should().BeTrue();
        restored.Provenance.Should().Be(proposedEdition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedWorkflowPreparesThenAppliesFinalAuthorReviewThroughBackupPrompt(bool namingSkipped)
    {
        const string libraryRoot = "C:\\Books";
        CalibreBook book = new(
            new(1), "Book", "Woods, Stuart",
            [new(new(1), "Woods, Stuart", "Woods, Stuart")], [],
            [new(
                "EPUB", "book.epub", "Woods, Stuart/Book (1)/book.epub",
                FormatFileStatus.Present,
                new(10, new(new string('a', 64))),
                new(10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))],
            "Woods, Stuart/Book (1)");
        LibrarySnapshot snapshot = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, libraryRoot),
            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero), [book], []);
        LibraryWorkflowSource source = new(
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")), new(3));
        LibraryState completed = WorkflowState(snapshot, LibraryWorkflowPhase.Completed, source);
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        A.CallTo(() => store.ListAsync(A<CancellationToken>._)).Returns([new(libraryRoot, snapshot.ScannedAt)]);
        ILibraryStateSession session = A.Fake<ILibraryStateSession>();
        A.CallTo(() => session.LoadAsync(libraryRoot, A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Success(completed));
        A.CallTo(() => session.GetCurrent(libraryRoot)).Returns(completed);
        IUnifiedCandidateCleanupExecutor executor = A.Fake<IUnifiedCandidateCleanupExecutor>();
        ExecuteUnifiedCandidateCleanupRequest? captured = null;
        A.CallTo(() => executor.ExecuteAsync(
                A<ExecuteUnifiedCandidateCleanupRequest>._,
                A<IProgress<UnifiedCandidateCleanupProgress>?>._,
                A<CancellationToken>._))
            .Invokes(call => captured = call.GetArgument<ExecuteUnifiedCandidateCleanupRequest>(0))
            .Returns(new UnifiedCandidateCleanupResult(
                new(Guid.Parse("99999999-8888-7777-6666-555555555555")),
                UnifiedCandidateCleanupState.Completed, 0, 0, 0, 0, [], UpdatedMetadataFieldCount: 1,
                RefreshedManagedNameCount: namingSkipped ? 0 : 1,
                VerifiedManagedNameBookIds: namingSkipped ? [] : [book.Id],
                SkippedManagedNames: namingSkipped
                    ? [new(book.Id, "managed_name_preflight_os_error_errno_13_winerror_32")] : []));
        IMetadataMutationConfirmationService confirmation = A.Fake<IMetadataMutationConfirmationService>();
        A.CallTo(() => confirmation.ConfirmAuthorNormalization(1)).Returns(true);
        CapturingLogger<MainWindowViewModel> logger = new();
        IQuarantineDestinationInspector destination = A.Fake<IQuarantineDestinationInspector>();
        A.CallTo(() => destination.InspectAsync(
                libraryRoot, libraryRoot + " - Unresolvable", A<CancellationToken>._))
            .Returns(new QuarantineDestinationInspection(new(false, null)));
        IFrontMatterAuthorCache frontCache = A.Fake<IFrontMatterAuthorCache>();
        A.CallTo(() => frontCache.TryReadAsync(A<FrontMatterAuthorCacheKey>._, A<CancellationToken>._))
            .Returns(new FrontMatterAuthorObservation(
                "EPUB", book.Formats[0].Fingerprint!, FrontMatterAuthorVersions.EpubExtractor,
                FrontMatterAuthorVersions.Limits, FrontMatterAuthorStatus.Missing, [], 1, 1, 0, false));
        IQuarantineCleanupExecutor quarantine = A.Fake<IQuarantineCleanupExecutor>();
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out ILibraryPathResolver pathResolver, out _, out _, new(store), session,
            workflowOptions: LibraryWorkflowOptions.Staged,
            candidateExecutor: executor,
            metadataConfirmation: confirmation,
            enableFinalAuthorReview: true,
            logger: logger,
            frontMatterCache: frontCache,
            quarantineDestination: destination,
            quarantineExecutor: quarantine);
        A.CallTo(() => pathResolver.ValidateAsync(libraryRoot, A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(new(libraryRoot, $"{libraryRoot}\\metadata.db")));
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);

        viewModel.CandidateCleanupAutomationName.Should().Be("Prepare final author review");
        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);

        captured.Should().BeNull();
        A.CallTo(() => confirmation.ConfirmAuthorNormalization(A<int>._)).MustNotHaveHappened();
        viewModel.FinalAuthorReviewFilterMode = FinalAuthorReviewFilterMode.All;
        FinalAuthorReviewSubjectRowViewModel row = viewModel.FinalAuthorReviewSubjects.Should().ContainSingle().Subject;
        row.Apply.Should().BeTrue();
        viewModel.UpdateFinalAuthorReviewSelection([row]);
        viewModel.UncheckSelectedFinalAuthorApplyCommand.Execute(null);
        row = viewModel.FinalAuthorReviewSubjects.Should().ContainSingle().Subject;
        row.Apply.Should().BeFalse();
        viewModel.UpdateFinalAuthorReviewSelection([row]);
        viewModel.CheckSelectedFinalAuthorApplyCommand.Execute(null);
        viewModel.FinalAuthorReviewSubjects.Should().ContainSingle().Which.Apply.Should().BeTrue();
        viewModel.CandidateCleanupAutomationName.Should().Be("Apply checked author corrections");

        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);

        captured.Should().NotBeNull();
        captured!.NormalizeAuthors.Should().BeTrue();
        captured.FinalAuthorReview.Should().NotBeNull();
        captured.MetadataReview.Should().BeNull();
        captured.GroupSelections.Should().BeEmpty();
        viewModel.CandidateCleanupResultSummary.Should().StartWith(
            $"Updated 1 of 1 checked author correction(s); refreshed {(namingSkipped ? 0 : 1)} managed name(s); "
            + $"skipped 0 unchanged metadata field(s); skipped {(namingSkipped ? 1 : 0)} unchanged managed name(s).");
        if (namingSkipped)
        {
            viewModel.CandidateCleanupResultSummary.Should().Contain("record ID 1")
                .And.Contain("managed_name_preflight_os_error_errno_13_winerror_32");
            viewModel.StatusMessage.Should().Contain("skipped").And.Contain("Continue");
        }
        else
            viewModel.StatusMessage.Should().Be("Final author corrections completed.");
        logger.Entries.Should().Contain(value =>
            value.EventId.Name == "UserVisibleOperationResult"
            && value.Message == viewModel.CandidateCleanupResultSummary);

        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);
        viewModel.ShowAllQuarantineRecords = true;
        QuarantineReviewSubjectRowViewModel disposition = viewModel.QuarantineReviewSubjects
            .Should().ContainSingle().Subject;
        disposition.Include.Should().BeFalse();
        viewModel.QuarantineVerificationSummary.Should().Contain("Retained input 1").And.Contain("pending 1");
        viewModel.UpdateQuarantineReviewSelection([disposition]);
        if (namingSkipped)
        {
            viewModel.AcceptSelectedQualificationCommand.CanExecute(null).Should().BeFalse(
                "a skipped managed name cannot become qualified through reviewer acceptance");
            disposition.Include.Should().BeFalse();
            return;
        }
        viewModel.AcceptSelectedQualificationCommand.Execute(null);
        viewModel.QuarantineVerificationSummary.Should().Contain("qualified 1").And.Contain("pending 0");
        await viewModel.CandidateCleanupCommand.ExecuteAsync(null);
        viewModel.StatusMessage.Should().Contain("High-quality library complete");
        A.CallTo(() => quarantine.ExecuteAsync(
            A<ExecuteQuarantineCleanupRequest>._, A<IProgress<QuarantineCleanupProgress>?>._,
            A<CancellationToken>._)).MustNotHaveHappened();
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

    [Theory]
    [InlineData(LibraryWorkflowPhase.ExactReady, MatchingEvidenceStatus.Unavailable, "Exact cleanup")]
    [InlineData(LibraryWorkflowPhase.ExactReady, MatchingEvidenceStatus.Stale, "Exact cleanup")]
    [InlineData(LibraryWorkflowPhase.CandidatePreparationReady, MatchingEvidenceStatus.Unavailable, "Prepare Candidate review")]
    [InlineData(LibraryWorkflowPhase.CandidatePreparationReady, MatchingEvidenceStatus.Stale, "Prepare Candidate review")]
    [InlineData(LibraryWorkflowPhase.CandidateCleanupCompleted, MatchingEvidenceStatus.Unavailable, "metadata review")]
    [InlineData(LibraryWorkflowPhase.CandidateCleanupCompleted, MatchingEvidenceStatus.Stale, "metadata review")]
    [InlineData(LibraryWorkflowPhase.Completed, MatchingEvidenceStatus.Unavailable, "final review")]
    [InlineData(LibraryWorkflowPhase.Completed, MatchingEvidenceStatus.Stale, "final review")]
    public async Task StagedExpandedSummaryExplainsTheActualNextStepInsteadOfRepeatingScan(
        LibraryWorkflowPhase phase, MatchingEvidenceStatus evidence, string nextStep)
    {
        const string root = "C:\\Books";
        LibrarySnapshot baseline = Snapshot(root);
        LibrarySnapshot snapshot = new(
            baseline.Identity, baseline.ScannedAt, baseline.Books, [],
            matchingRunSummary: new(MatchingPolicyVersion.Current, evidence, 1, 0, 0, 0, 0, 0, 0, 0, 0));
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        A.CallTo(() => store.ListAsync(A<CancellationToken>._)).Returns([new(root, snapshot.ScannedAt)]);
        LibraryState state = WorkflowState(snapshot, phase, new(
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")), new(0)));
        ILibraryStateSession session = A.Fake<ILibraryStateSession>();
        A.CallTo(() => session.LoadAsync(root, A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Success(state));
        A.CallTo(() => session.GetCurrent(root)).Returns(state);
        using MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), session,
            workflowOptions: LibraryWorkflowOptions.Staged);
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = root;

        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);

        viewModel.ExpandedCandidateSummary.Should().Contain(nextStep).And.NotContain("Run a fresh scan");
        viewModel.ExpandedCandidateGroups.Should().BeEmpty();
        viewModel.ErrorMessage.Should().BeEmpty();
        session.GetCurrent(root).Should().BeSameAs(state);
    }

    [Theory]
    [InlineData("candidate-review")]
    [InlineData("uncertain")]
    [InlineData("incompatible-checkpoint")]
    [InlineData("different-snapshot")]
    public async Task ExpandedSummaryDoesNotHideMissingEvidenceBehindAnUntrustedWorkflow(string scenario)
    {
        const string root = "C:\\Books";
        LibrarySnapshot snapshot = Snapshot(root);
        LibraryWorkflowSource source = new(new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")), new(0));
        LibraryState loaded = WorkflowState(snapshot,
            scenario == "candidate-review" ? LibraryWorkflowPhase.CandidateAnalysisReady
                : LibraryWorkflowPhase.CandidateCleanupCompleted, source);
        LibraryState current = loaded;
        if (scenario == "uncertain")
        {
            current = loaded = new(
                loaded.GenerationId, loaded.Revision, LibraryStateStatus.Uncertain, snapshot, loaded.ProjectedAtUtc,
                new("TEST.UNCERTAIN", "Generated ambiguous mutation.", loaded.ProjectedAtUtc),
                loaded.WorkflowCheckpoint);
        }
        else if (scenario == "incompatible-checkpoint")
        {
            current = loaded = new(
                loaded.GenerationId, loaded.Revision, LibraryStateStatus.Authoritative, snapshot, loaded.ProjectedAtUtc,
                workflowCheckpoint: new(
                    LibraryWorkflowPhase.CandidateCleanupCompleted, loaded.GenerationId, loaded.Revision,
                    new("old-workflow", "old-analysis", "old-cleanup", "old-candidates"),
                    loaded.ProjectedAtUtc, source));
        }
        else if (scenario == "different-snapshot")
        {
            LibrarySnapshot changed = new(snapshot.Identity, snapshot.ScannedAt, snapshot.Books, []);
            current = WorkflowState(changed, LibraryWorkflowPhase.CandidateCleanupCompleted, source);
        }
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        A.CallTo(() => store.ListAsync(A<CancellationToken>._)).Returns([new(root, snapshot.ScannedAt)]);
        ILibraryStateSession session = A.Fake<ILibraryStateSession>();
        A.CallTo(() => session.LoadAsync(root, A<CancellationToken>._))
            .Returns(LibraryStateSessionOutcome.Success(loaded));
        A.CallTo(() => session.GetCurrent(root)).Returns(current);
        using MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), session,
            workflowOptions: LibraryWorkflowOptions.Staged);
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = root;

        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);

        viewModel.ExpandedCandidateSummary.Should().Contain("Run a fresh scan")
            .And.NotContain("Continue with online metadata review");
        viewModel.ExpandedCandidateGroups.Should().BeEmpty();
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
        viewModel.ExpandedCandidateSummary.Should().Contain("1 expanded groups");
        viewModel.ExpandedCandidateSummary.Should().Contain("Open Library bibliographic evidence was disabled");
        viewModel.ExpandedCandidateSummary.Should().Contain("Local embedding observations were disabled");
        viewModel.SelectedExpandedCandidateMembers.Should().HaveCount(2);
        viewModel.SelectedExpandedCandidateGroup!.KeeperRecordId.Should().Be(2);
        viewModel.SelectedExpandedCandidateMember!.BookId.Should().Be(2);
        viewModel.SelectedExpandedCandidateMembers[1].Action.Should().Be("Keep");
        viewModel.SelectedExpandedCandidateMembers[0].Action.Should().Be("Remove");
        viewModel.SelectedExpandedCandidateMember = viewModel.SelectedExpandedCandidateMembers[0];
        viewModel.SelectedExpandedCandidateMembers[0].Action.Should().Be("Remove");
        viewModel.SelectedExpandedCandidateMembers[1].Action.Should().Be("Keep");
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
        LibraryOperationCoordinator operationCoordinator = new();
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), stateSession,
            operationCoordinator: operationCoordinator);
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;

        Task load = viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.StatusMessage == "Loading saved library analysis...");

        viewModel.IsBusy.Should().BeTrue();
        viewModel.IsProgressIndeterminate.Should().BeTrue();
        viewModel.ProgressPercentage.Should().Be(0);
        viewModel.Books.Should().BeEmpty();
        operationCoordinator.IsOperationActive.Should().BeTrue();
        operationCoordinator.TryBegin().Should().BeNull();

        pendingLoad.SetResult(LibraryStateSessionOutcome.Success(new(
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")),
            new(7), LibraryStateStatus.Authoritative, snapshot, snapshot.ScannedAt.AddMinutes(1))));
        await load;

        viewModel.IsProgressIndeterminate.Should().BeFalse();
        viewModel.ProgressPercentage.Should().Be(100);
        viewModel.Books.Should().ContainSingle();
        operationCoordinator.IsOperationActive.Should().BeFalse();
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
    public async Task ScanShowsActiveFeedbackWhileAuthoritativeStateIsPersisting()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        ILibraryStateSession stateSession = A.Fake<ILibraryStateSession>();
        TaskCompletionSource<LibrarySnapshot> persistenceStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<LibraryStateSessionOutcome> persistence = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        A.CallTo(() => stateSession.StartFromExactAnalysisAsync(
                A<LibrarySnapshot>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                persistenceStarted.TrySetResult(call.GetArgument<LibrarySnapshot>(0)!);
                return persistence.Task;
            });
        A.CallTo(() => store.ListAsync(A<CancellationToken>._)).Returns([]);
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        MainWindowViewModel viewModel = CreateViewModel(
            picker,
            out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader,
            out IFormatFileHasher hasher,
            new(store),
            stateSession,
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
            .Returns(ResolvedFormatPathOutcome.Success(new("library", "full", "Book/Book.epub")));
        A.CallTo(() => hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .Returns([FormatHashResult.Failure(0, FormatHashResultStatus.Missing, "missing")]);
        await viewModel.SelectLibraryCommand.ExecuteAsync(null);

        Task scan = viewModel.ScanCommand.ExecuteAsync(null);
        LibrarySnapshot snapshot = await persistenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        viewModel.IsBusy.Should().BeTrue();
        viewModel.IsProgressIndeterminate.Should().BeTrue();
        viewModel.StatusMessage.Should().Be("Saving authoritative scan state...");
        LibraryState state = LibraryState.FromScan(snapshot, new(Guid.NewGuid()))
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, snapshot.ScannedAt);
        persistence.SetResult(LibraryStateSessionOutcome.Success(state));
        await scan;
    }

    [Fact]
    public async Task PickerCancellationLeavesSelectionUnchanged()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        LibraryOperationCoordinator operationCoordinator = new();
        A.CallTo(() => picker.PickFolder(A<string?>._)).ReturnsLazily(() =>
        {
            operationCoordinator.IsOperationActive.Should().BeTrue();
            operationCoordinator.TryBegin().Should().BeNull();
            return null;
        });
        MainWindowViewModel viewModel = CreateViewModel(
            picker, out _, out _, out _, operationCoordinator: operationCoordinator);

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);

        viewModel.SelectedLibraryPath.Should().BeEmpty();
        operationCoordinator.IsOperationActive.Should().BeFalse();
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
        alternateKeeper.Action.Should().Be("Remove");
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
        IUnifiedCandidateCleanupConfirmationService? candidateConfirmation = null,
        LibraryOperationCoordinator? operationCoordinator = null,
        IOnlineMetadataSettingsDialogService? onlineMetadataSettingsDialog = null,
        PrepareMetadataReviewUseCase? prepareMetadataReview = null,
        IMetadataMutationConfirmationService? metadataConfirmation = null,
        bool enableFinalAuthorReview = false,
        ILogger<MainWindowViewModel>? logger = null,
        IFinalAuthorCatalogReconciler? finalAuthorCatalogReconciler = null,
        IFrontMatterAuthorCache? frontMatterCache = null,
        IQuarantineDestinationInspector? quarantineDestination = null,
        IQuarantineCleanupExecutor? quarantineExecutor = null,
        IQuarantineCleanupConfirmationService? quarantineConfirmation = null)
    {
        resolver = A.Fake<ILibraryPathResolver>();
        reader = A.Fake<ICalibreMetadataReader>();
        IClock clock = A.Fake<IClock>();
        hasher = A.Fake<IFormatFileHasher>();
        InspectEmbeddedAuthorMetadataUseCase? embeddedAuthors = enableFinalAuthorReview
            ? new(
                resolver,
                A.Fake<IEpubInspector>(),
                A.Fake<IPdfAuthorMetadataInspector>(),
                A.Fake<IEmbeddedAuthorMetadataCache>())
            : null;
        InspectFrontMatterAuthorsUseCase? frontMatterAuthors = enableFinalAuthorReview
            ? new(
                resolver,
                A.Fake<IEpubFrontMatterAuthorInspector>(),
                A.Fake<IPdfFrontMatterAuthorInspector>(),
                frontMatterCache ?? A.Fake<IFrontMatterAuthorCache>())
            : null;
        if (enableFinalAuthorReview && finalAuthorCatalogReconciler is null)
        {
            finalAuthorCatalogReconciler = A.Fake<IFinalAuthorCatalogReconciler>();
            A.CallTo(() => finalAuthorCatalogReconciler.ExecuteAsync(
                    A<LibraryState>._, A<CancellationToken>._))
                .ReturnsLazily(call => Task.FromResult(new FinalAuthorCatalogReconciliationResult(
                    call.GetArgument<LibraryState>(0),
                    0)));
        }
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
            unifiedCandidateConfirmation: candidateConfirmation,
            operationCoordinator: operationCoordinator,
            onlineMetadataSettingsDialog: onlineMetadataSettingsDialog,
            prepareMetadataReview: prepareMetadataReview,
            metadataMutationConfirmation: metadataConfirmation,
            inspectEmbeddedAuthorMetadata: embeddedAuthors,
            inspectFrontMatterAuthors: frontMatterAuthors,
            logger: logger,
            quarantineDestinationInspector: quarantineDestination,
            quarantineCleanupExecutor: quarantineExecutor,
            quarantineCleanupConfirmation: quarantineExecutor is null
                ? null : quarantineConfirmation ?? A.Fake<IQuarantineCleanupConfirmationService>(),
            reconcileFinalAuthorCatalog: finalAuthorCatalogReconciler);
    }

    private static BookFormat ViewerFormat(
        string format,
        FormatFileStatus status = FormatFileStatus.Present,
        string? relativePath = null) => new(
        format, "Book", relativePath ?? $"Author/Book.{format.ToLowerInvariant()}", status,
        status == FormatFileStatus.Present ? new(100, new(new string('a', 64))) : null,
        status == FormatFileStatus.Present ? new(100, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0) : null);

    private static async Task<(MainWindowViewModel ViewModel, IEbookViewerLauncher Viewer)> LoadViewerSnapshotAsync(
        IReadOnlyList<BookFormat> formats)
    {
        const string libraryRoot = "C:\\Books";
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        IEbookViewerLauncher viewer = A.Fake<IEbookViewerLauncher>();
        LibrarySnapshot baseline = Snapshot(libraryRoot);
        LibrarySnapshot snapshot = new(baseline.Identity, baseline.ScannedAt,
            [new(new(1), "Book", "Author", [], [], formats, "Author")], []);
        A.CallTo(() => store.ListAsync(A<CancellationToken>._)).Returns([new(libraryRoot, snapshot.ScannedAt)]);
        A.CallTo(() => store.ReadAsync(libraryRoot, A<CancellationToken>._)).Returns(snapshot);
        A.CallTo(() => viewer.LaunchAsync(A<EbookViewerLaunchRequest>._, A<CancellationToken>._))
            .Returns(EbookViewerLaunchResult.Success());
        MainWindowViewModel viewModel = CreateViewModel(
            A.Fake<ILibraryFolderPicker>(), out _, out _, out _, new(store), ebookViewer: viewer);
        await viewModel.InitializeAsync();
        viewModel.SelectedPersistedLibraryPath = libraryRoot;
        await viewModel.LoadPersistedSnapshotCommand.ExecuteAsync(null);
        viewModel.ErrorMessage.Should().BeEmpty();
        return (viewModel, viewer);
    }

    private static PrepareMetadataReviewUseCase CreateMetadataReviewUseCase(
        List<MetadataReviewDecision> persisted,
        Action? onProviderSearch = null,
        Action<EditionMetadataLookupQuery>? onProviderQuery = null)
    {
        IEditionMetadataProvider openLibrary = Provider(
            "open-library", "open-library/1.0.0", onProviderSearch, onProviderQuery);
        IEditionMetadataProvider googleBooks = Provider(
            "google-books", "google-books/1.0.0", onProviderSearch, onProviderQuery);
        IEditionMetadataProposalCache cache = A.Fake<IEditionMetadataProposalCache>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        ResolveEditionMetadataProposalsUseCase resolver = new(
            [openLibrary, googleBooks], cache, clock, new());
        IMetadataReviewDecisionStore store = A.Fake<IMetadataReviewDecisionStore>();
        A.CallTo(() => store.ReadAsync(A<string>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult<IReadOnlyList<MetadataReviewDecision>>(persisted.ToArray()));
        A.CallTo(() => store.WriteAsync(
                A<string>._,
                A<IReadOnlyList<MetadataReviewDecision>>._,
                A<CancellationToken>._))
            .Invokes(call =>
            {
                persisted.Clear();
                persisted.AddRange(call.GetArgument<IReadOnlyList<MetadataReviewDecision>>(1)!);
            })
            .Returns(Task.CompletedTask);
        return new(resolver, store);

        static IEditionMetadataProvider Provider(
            string id, string version, Action? onSearch, Action<EditionMetadataLookupQuery>? onQuery)
        {
            EditionMetadataProviderIdentity identity = new(id, version);
            IEditionMetadataProvider provider = A.Fake<IEditionMetadataProvider>();
            A.CallTo(() => provider.Identity).Returns(identity);
            A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
                .ReturnsLazily(call =>
                {
                    onSearch?.Invoke();
                    EditionMetadataLookupQuery query = call.GetArgument<EditionMetadataLookupQuery>(0)!;
                    onQuery?.Invoke(query);
                    return Task.FromResult(new EditionMetadataSearchResult(
                        identity,
                        EditionMetadataSearchStatus.Success,
                        new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero),
                        [new(
                            id + "-work",
                            id + "-edition",
                            query.Title!,
                            query.Authors,
                            [new EditionMetadataIdentifier("isbn", "9780306406157")],
                            "Publisher",
                            new(2005),
                            ["en"])]));
                });
            return provider;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, eventId, formatter(state, exception)));
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
