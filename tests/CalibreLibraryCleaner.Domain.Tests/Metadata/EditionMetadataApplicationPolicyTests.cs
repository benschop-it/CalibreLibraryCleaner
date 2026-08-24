using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class EditionMetadataApplicationPolicyTests
{
    [Fact]
    public void FullyAppliedCandidateHasNoApplicableFields()
    {
        CalibreBook book = Book();
        EditionMetadataCandidate candidate = Candidate();

        EditionMetadataApplicationPolicy.ApplicableFields(book, candidate).Should().BeEmpty();
    }

    [Fact]
    public void ReturnsOnlyFieldsThatWouldChangeCurrentMetadata()
    {
        CalibreBook book = Book(publisher: "Old Publisher", hasCover: false);
        EditionMetadataCandidate candidate = Candidate();

        EditionMetadataApplicationPolicy.ApplicableFields(book, candidate)
            .Should().Equal(LibraryMetadataField.Publisher, LibraryMetadataField.Cover);
    }

    private static CalibreBook Book(string publisher = "Publisher", bool hasCover = true) => new(
        new(1),
        "Title",
        "Author, Alice",
        [new(new(1), "Alice Author", "Author, Alice")],
        [new("isbn", "9780306406157"), new("google", "preserved")],
        [],
        "Author/Title (1)",
        new(
            publisher,
            new DateTimeOffset(2005, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "Series",
            2,
            ["eng"],
            hasCover));

    private static EditionMetadataCandidate Candidate() => new(
        "work",
        "edition",
        "Title",
        ["Alice Author"],
        [new("isbn", "9780306406157")],
        "Publisher",
        new(2005),
        ["en"],
        "Series",
        2,
        new("cover", "https://example.test/cover.jpg"));
}
