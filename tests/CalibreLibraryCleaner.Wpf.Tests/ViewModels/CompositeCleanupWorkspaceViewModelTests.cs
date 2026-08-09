using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class CompositeCleanupWorkspaceViewModelTests
{
    [Fact]
    public async Task ConflictsShowModalBeforeConfirmationOrWorkerStartup()
    {
        Fixture fixture = await Fixture.CreateAsync();
        ICompositeCleanupDialogService dialog = A.Fake<ICompositeCleanupDialogService>();
        CompositeCleanupWorkspaceViewModel viewModel = new(fixture.UseCase, dialog);
        MetadataDuplicateGroupRowViewModel metadata = new(
            fixture.Metadata, fixture.Books);
        ExpandedCandidateRetentionDecision retention = ExpandedCandidateRetentionPolicy.Select(
            [fixture.Expanded], fixture.Books.Values).Single();
        ExpandedCandidateGroupRowViewModel expanded = new(
            fixture.Expanded, fixture.Books, retention);
        metadata.KeeperMember = metadata.Members.Single(value => value.BookId == 1);
        expanded.KeeperMember = expanded.Members.Single(value => value.BookId == 2);
        viewModel.UpdateContext(fixture.Snapshot, [], [metadata], [expanded]);

        await viewModel.CleanupAllCommand.ExecuteAsync(null);

        A.CallTo(() => dialog.ShowConflicts(A<string>.That.Contains("COMPOSITE")))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => dialog.ConfirmExternalBackup(A<CompositeCleanupPlanSummary>._))
            .MustNotHaveHappened();
        A.CallTo(() => fixture.Workers.TryOpenAsync(
            A<OpenCalibreMutationWorkerRequest>._, A<CancellationToken>._)).MustNotHaveHappened();
        viewModel.Status.Should().Contain("blocked");
    }

    private sealed record Fixture(
        LibrarySnapshot Snapshot,
        ExactMetadataDuplicateGroup Metadata,
        WorkLanguageCandidateGroup Expanded,
        IReadOnlyDictionary<CalibreBookId, CalibreBook> Books,
        ICalibreMutationWorkerFactory Workers,
        ExecuteCompositeCleanupUseCase UseCase)
    {
        public static async Task<Fixture> CreateAsync()
        {
            CalibreBook first = Book(1, 'a');
            CalibreBook second = Book(2, 'b');
            ExactMetadataDuplicateGroup metadata = ExactMetadataDuplicateDetector.Detect([first, second]).Single();
            WorkLanguageCandidateGroup expanded = WorkLanguageCandidateGroup.Create(
                "en", [first.Id, second.Id], [first.Id, second.Id],
                WorkLanguageCandidateConfidence.Strong,
                [new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor)],
                contentComparison: new(1, 1, 0, 0, 0, 0));
            LibrarySnapshot snapshot = new(
                new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\Library"),
                DateTimeOffset.UnixEpoch,
                [first, second], [], exactMetadataDuplicateGroups: [metadata],
                workLanguageCandidateGroups: [expanded],
                matchingRunSummary: new(MatchingPolicyVersion.Current,
                    MatchingEvidenceStatus.Available, 2, 1, 1, 0, 2, 0, 1, 1, 0));
            ILibraryStateStore store = A.Fake<ILibraryStateStore>();
            LibraryStateSession state = new(store);
            await state.StartFromScanAsync(snapshot, CancellationToken.None);
            ICalibreMutationWorkerFactory workers = A.Fake<ICalibreMutationWorkerFactory>();
            ExecuteCompositeCleanupUseCase useCase = new(
                state,
                A.Fake<ICalibreToolDiscovery>(),
                workers,
                A.Fake<ILibraryMutationLease>(),
                A.Fake<ICleanupExecutionIdGenerator>(),
                A.Fake<IClock>());
            return new(snapshot, metadata, expanded,
                snapshot.Books.ToDictionary(value => value.Id), workers, useCase);
        }

        private static CalibreBook Book(long id, char digest) => new(
            new(id), "Shared Book", "Author", [new(new(id), "Author", "Author")], [],
            [new("EPUB", "book", $"Author/Book{id}/book.epub", FormatFileStatus.Present,
                new(1_024, new(new string(digest, 64))),
                new(1_024, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))],
            $"Author/Book{id}", new(languages: ["eng"]));
    }
}
