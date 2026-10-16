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

    [Fact]
    public void AutomaticApplyAllowsAdditiveFieldsButRejectsReplacingPopulatedMetadata()
    {
        CalibreBook sparse = new(new(1), "Title", "Author, Alice",
            [new(new(1), "Alice Author", "Author, Alice")], [new("google", "preserved")], [], "Author/Title (1)");
        EditionMetadataCandidate additive = new("work", "edition", "Title", ["Alice Author"],
            [new("isbn", "9780306406157")], "Publisher", new(2005), ["en"], "Series", 2,
            new("cover", "https://example.test/cover.jpg"));

        EditionMetadataApplicationPolicy.CanApplyAutomatically(sparse, additive).Should().BeTrue();
        EditionMetadataApplicationPolicy.CanApplyAutomatically(
            Book(), Candidate() with { }).Should().BeTrue();
        EditionMetadataApplicationPolicy.CanApplyAutomatically(
            Book(), new("work", "edition", "OTHER TITLE", ["Alice Author"],
                [new("isbn", "9780306406157")], "Publisher", new(2005), ["en"], "Series", 2)).Should().BeFalse();
        EditionMetadataApplicationPolicy.CanApplyAutomatically(
            Book(), new("work", "edition", "Title", ["Alice Author"],
                [new("isbn", "9780140328721")], "Publisher", new(2005), ["en"], "Series", 2)).Should().BeFalse();
    }

    [Fact]
    public void MergePreviewPreservesCurrentIdentifiersAndCoverAndUsesApplyState()
    {
        CalibreBook book = Book();
        EditionMetadataCandidate candidate = new("work", "edition", "New Title", ["New Author"],
            [new("isbn", "9780140328721")], null, languages: [], cover: null);

        EditionMetadataMergePreview uncheckedPreview = EditionMetadataApplicationPolicy.Preview(book, candidate, false);
        EditionMetadataMergePreview appliedPreview = EditionMetadataApplicationPolicy.Preview(book, candidate, true);

        uncheckedPreview.Title.Should().Be("Title");
        uncheckedPreview.Authors.Should().Equal("Alice Author");
        appliedPreview.Title.Should().Be("New Title");
        appliedPreview.Authors.Should().Equal("New Author");
        appliedPreview.Identifiers.Should().Contain("google:preserved").And.Contain("isbn:9780140328721");
        appliedPreview.HasCover.Should().BeTrue();
        appliedPreview.Publisher.Should().Be("Publisher");
        appliedPreview.Languages.Should().Equal("eng");
    }

    [Fact]
    public void MissingCandidateFieldsNeverReplacePopulatedCurrentFields()
    {
        CalibreBook book = Book();
        EditionMetadataCandidate sparse = new(
            "work", "edition", "Title", ["Alice Author"], identifiers: [],
            publisher: null, publicationDate: null, languages: [], series: null,
            seriesIndex: null, cover: null);

        EditionMetadataApplicationPolicy.ApplicableFields(book, sparse).Should().BeEmpty();
        EditionMetadataMergePreview preview = EditionMetadataApplicationPolicy.Preview(book, sparse, true);
        preview.Publisher.Should().Be("Publisher");
        preview.PublicationDate.Should().Be(new DateTimeOffset(2005, 1, 1, 0, 0, 0, TimeSpan.Zero));
        preview.Languages.Should().Equal("eng");
        preview.Series.Should().Be("Series");
        preview.HasCover.Should().BeTrue();
        preview.Identifiers.Should().Contain("google:preserved").And.Contain("isbn:9780306406157");
    }

    [Fact]
    public void CandidateDroppingCurrentAuthorAndChangingEditionFactsRequiresReview()
    {
        CalibreBook book = new(
            new(1), "Met gevoelens van sympathie", "Sagan, Françoise & Buckinx, Théo",
            [new(new(1), "Françoise Sagan", "Sagan, Françoise"), new(new(2), "Théo Buckinx", "Buckinx, Théo")],
            [new("google", "preserved"), new("isbn", "9789055422593")], [], "Author/Book (1)",
            new("Stichting Uitgeverij XL", new DateTimeOffset(1997, 5, 15, 0, 0, 0, TimeSpan.Zero),
                languages: ["nld"], hasCover: true));
        EditionMetadataCandidate candidate = new(
            "work", "edition", "MET GEVOELENS VAN SYMPATHIE", ["Françoise Sagan"],
            [new("isbn", "9789068013887")], "Atlas-Contact", new(1994, 6, 2));

        EditionMetadataApplicationPolicy.CanApplyAutomatically(book, candidate).Should().BeFalse();
        EditionMetadataMergePreview preview = EditionMetadataApplicationPolicy.Preview(book, candidate, true);
        preview.Identifiers.Should().Contain("google:preserved").And.Contain("isbn:9789068013887");
        preview.HasCover.Should().BeTrue();
        preview.Authors.Should().Equal("Françoise Sagan");
    }

    [Theory]
    [InlineData("TITLE", true)]
    [InlineData("Title", false)]
    [InlineData("1984", false)]
    [InlineData("NATO and Europe", false)]
    public void AllCapsDetectionIsBoundedToActualCasedText(string value, bool expected)
    {
        EditionMetadataApplicationPolicy.IsAllCaps(value).Should().Be(expected);
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
