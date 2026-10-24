using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class ReconcileFinalAuthorCatalogUseCaseTests
{
    [Fact]
    public async Task SharedAuthorSideEffectsAreProjectedForEveryAffectedRecord()
    {
        Harness harness = new(nonAuthorChange: false);

        FinalAuthorCatalogReconciliationResult result = await harness.ExecuteAsync();

        result.IsSuccess.Should().BeTrue(result.FailureCode);
        result.ReconciledRecordCount.Should().Be(2);
        result.State!.IsWorkflowCheckpointCurrent.Should().BeTrue();
        result.State.Snapshot.Books.Should().OnlyContain(book =>
            book.Authors.Single().Name == "Updated Author"
            && book.Authors.Single().SortName == "Author, Updated"
            && book.AuthorSort == "Author, Updated");
        harness.Applied.Should().HaveCount(2)
            .And.OnlyContain(value => value is ReconcileAuthorMetadataLibraryStateDelta);
    }

    [Fact]
    public async Task NonAuthorDriftBlocksReconciliationWithoutProjection()
    {
        Harness harness = new(nonAuthorChange: true);

        FinalAuthorCatalogReconciliationResult result = await harness.ExecuteAsync();

        result.IsSuccess.Should().BeFalse();
        result.FailureCode.Should().Be("FINAL_AUTHOR_RECONCILIATION.NON_AUTHOR_STATE_CHANGED");
        harness.Applied.Should().BeEmpty();
    }

    private sealed class Harness
    {
        private const string Root = "C:\\Library";
        private readonly ReconcileFinalAuthorCatalogUseCase _useCase;
        private readonly LibraryState _initial;
        private LibraryState _current;

        public Harness(bool nonAuthorChange)
        {
            LibraryStateGenerationId generation = new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
            CalibreBook[] books = [Book(1), Book(2)];
            LibrarySnapshot snapshot = new(
                new("bbbbbbbb-cccc-dddd-eeee-ffffffffffff", 27, Root),
                DateTimeOffset.UnixEpoch,
                books,
                []);
            _initial = new(
                generation,
                new(0),
                LibraryStateStatus.Authoritative,
                snapshot,
                DateTimeOffset.UnixEpoch,
                workflowCheckpoint: new(
                    LibraryWorkflowPhase.Completed,
                    generation,
                    new(0),
                    LibraryWorkflowPolicyVersions.Current,
                    DateTimeOffset.UnixEpoch,
                    new(generation, new(0))));
            _current = _initial;
            ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
            ValidatedLibraryLocation location = new(Root, Path.Combine(Root, "metadata.db"));
            A.CallTo(() => resolver.ValidateAsync(Root, A<CancellationToken>._))
                .Returns(LibraryValidationOutcome.Success(location));
            ICalibreMetadataReader reader = A.Fake<ICalibreMetadataReader>();
            CalibreBookRecord[] live = books.Select(book => new CalibreBookRecord(
                book.Id.Value,
                nonAuthorChange && book.Id.Value == 2 ? "Changed Title" : book.Title,
                "Author, Updated",
                book.RelativeDirectory,
                [new(10, "Updated Author", "Author, Updated")],
                [],
                [new("EPUB", "catalog-stem-may-differ-from-physical-basename")],
                new(null, null, null, null, [], false))).ToArray();
            A.CallTo(() => reader.ReadAsync(location, null, A<CancellationToken>._))
                .Returns(CalibreCatalogReadOutcome.Success(new(
                    snapshot.Identity.CalibreLibraryUuid,
                    snapshot.Identity.SchemaVersion,
                    live)));
            ILibraryStateSession session = A.Fake<ILibraryStateSession>();
            A.CallTo(() => session.ApplyBatchAsync(
                    Root, A<IReadOnlyList<LibraryStateDelta>>._, CancellationToken.None))
                .Invokes(call =>
                {
                    IReadOnlyList<LibraryStateDelta> deltas = call.GetArgument<IReadOnlyList<LibraryStateDelta>>(1)!;
                    Applied.AddRange(deltas);
                    _current = LibraryStateDeltaPolicy.ApplyBatch(_current, deltas);
                })
                .ReturnsLazily(() => LibraryStateSessionOutcome.Success(_current));
            A.CallTo(() => session.AdvanceWorkflowAsync(
                    Root, LibraryWorkflowPhase.Completed, A<DateTimeOffset>._, CancellationToken.None))
                .Invokes(() => _current = _current.AdvanceWorkflow(
                    LibraryWorkflowPhase.Completed, _current.ProjectedAtUtc))
                .ReturnsLazily(() => LibraryStateSessionOutcome.Success(_current));
            A.CallTo(() => session.CheckpointAsync(Root, CancellationToken.None))
                .ReturnsLazily(() => LibraryStateSessionOutcome.Success(_current));
            IClock clock = A.Fake<IClock>();
            A.CallTo(() => clock.GetUtcNow()).Returns(DateTimeOffset.UnixEpoch.AddSeconds(1));
            _useCase = new(resolver, reader, session, clock);
        }

        public List<LibraryStateDelta> Applied { get; } = [];

        public Task<FinalAuthorCatalogReconciliationResult> ExecuteAsync() =>
            _useCase.ExecuteAsync(_initial, CancellationToken.None);

        private static CalibreBook Book(long id) => new(
            new(id),
            $"Book {id}",
            "Author, Original",
            [new(new(10), "Original Author", "Author, Original")],
            [],
            [new(
                "EPUB",
                "book.epub",
                $"Author/Book {id} ({id})/book.epub",
                FormatFileStatus.Present,
                new(10, new(new string('a', 64))),
                new(10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))],
            $"Author/Book {id} ({id})");
    }
}
