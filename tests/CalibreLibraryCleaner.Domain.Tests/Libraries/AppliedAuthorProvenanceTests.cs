using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Libraries;

public sealed class AppliedAuthorProvenanceTests
{
    [Fact]
    public void ProvenanceBindsToExactOrderedAuthorValues()
    {
        AppliedAuthorProvenance provenance = Provenance(["First Writer", "Second Writer"]);

        provenance.Matches(["First Writer", "Second Writer"]).Should().BeTrue();
        provenance.Matches(["Second Writer", "First Writer"]).Should().BeFalse();
        provenance.Matches(["First Writer"]).Should().BeFalse();
    }

    [Fact]
    public void AuthorDeltaPublishesMatchingProvenanceAndUnrelatedMetadataPreservesIt()
    {
        LibraryState state = State(Book("Unknown"));
        AppliedAuthorProvenance provenance = Provenance(["Reliable Writer"]);
        state = LibraryStateDeltaPolicy.Apply(state, new SetMetadataLibraryStateDelta(
            state.GenerationId,
            state.Revision,
            "authors",
            DateTimeOffset.UnixEpoch,
            new(1),
            LibraryMetadataField.Authors,
            ["Reliable Writer"],
            appliedAuthorProvenance: provenance));

        state.Snapshot.Books.Single().AppliedAuthorProvenance.Should().Be(provenance);

        state = LibraryStateDeltaPolicy.Apply(state, new SetMetadataLibraryStateDelta(
            state.GenerationId,
            state.Revision,
            "title",
            DateTimeOffset.UnixEpoch,
            new(1),
            LibraryMetadataField.Title,
            ["Changed Title"]));

        state.Snapshot.Books.Single().AppliedAuthorProvenance.Should().Be(provenance);
    }

    [Fact]
    public void AuthorReplacementWithoutProvenanceClearsTheMarker()
    {
        LibraryState state = State(new CalibreBook(
            new(1),
            "Title",
            "Reliable Writer",
            [new(new(1), "Reliable Writer", "Reliable Writer")],
            [],
            [],
            "Author/Title (1)",
            appliedAuthorProvenance: Provenance(["Reliable Writer"])));

        state = LibraryStateDeltaPolicy.Apply(state, new SetMetadataLibraryStateDelta(
            state.GenerationId,
            state.Revision,
            "authors",
            DateTimeOffset.UnixEpoch,
            new(1),
            LibraryMetadataField.Authors,
            ["Different Writer"]));

        state.Snapshot.Books.Single().AppliedAuthorProvenance.Should().BeNull();
    }

    private static AppliedAuthorProvenance Provenance(IEnumerable<string> authors) => new(
        "provider",
        "provider/1.0.0",
        "edition",
        "proposal/1.0.0",
        AppliedAuthorProvenance.CreateValuesIdentity(authors));

    private static CalibreBook Book(string author) => new(
        new(1),
        "Title",
        author,
        [new(new(1), author, author)],
        [],
        [],
        "Author/Title (1)");

    private static LibraryState State(CalibreBook book)
    {
        LibrarySnapshot snapshot = new(
            new("library", 27, "C:\\library"),
            DateTimeOffset.UnixEpoch,
            [book],
            []);
        return LibraryState.FromScan(
            snapshot,
            new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
    }
}
