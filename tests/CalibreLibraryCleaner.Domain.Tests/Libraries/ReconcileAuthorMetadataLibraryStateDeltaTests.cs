using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Libraries;

public sealed class ReconcileAuthorMetadataLibraryStateDeltaTests
{
    [Fact]
    public void ReconciliationUpdatesOnlyLiveAuthorState()
    {
        LibraryState state = State();
        BookAuthor liveAuthor = new(new(11), "Updated Author", "Author, Updated");

        LibraryState reconciled = LibraryStateDeltaPolicy.Apply(state, new ReconcileAuthorMetadataLibraryStateDelta(
            state.GenerationId,
            state.Revision,
            "reconcile-authors:1",
            state.ProjectedAtUtc,
            new(1),
            "Author, Updated",
            [liveAuthor]));

        CalibreBook book = reconciled.Snapshot.Books.Should().ContainSingle().Subject;
        book.Authors.Should().Equal(liveAuthor);
        book.AuthorSort.Should().Be("Author, Updated");
        book.Title.Should().Be("The Book");
        book.RelativeDirectory.Should().Be("Original/Book (1)");
        book.Formats.Should().BeEquivalentTo(state.Snapshot.Books.Single().Formats);
    }

    private static LibraryState State()
    {
        LibraryStateGenerationId generation = new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        CalibreBook book = new(
            new(1),
            "The Book",
            "Original, Author",
            [new(new(10), "Original Author", "Author, Original")],
            [],
            [],
            "Original/Book (1)");
        LibrarySnapshot snapshot = new(
            new("bbbbbbbb-cccc-dddd-eeee-ffffffffffff", 27, "C:\\Library"),
            DateTimeOffset.UnixEpoch,
            [book],
            []);
        return new(
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
    }
}
