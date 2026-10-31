using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class EditionMetadataApplicationPolicyTests
{
    [Theory]
    [InlineData("Lannert| Stacy| Kemp| Kristen")]
    [InlineData("Stacy Lannert")]
    public void LegacyCoauthorOrderIsAppliedOnlyWhenTheCompleteCurrentCreditsEstablishIt(string current)
    {
        BookAuthor[] credits = current.Contains('|', StringComparison.Ordinal)
            ? [new(new(1), current, "Source Sort")]
            : [new(new(1), "Stacy Lannert", "Lannert, Stacy"), new(new(2), "Kristen Kemp", "Kemp, Kristen")];
        CalibreBook book = new(new(1), "Title", "Source Sort", credits, [], [], "Author/Title (1)");
        EditionMetadataCandidate legacy = new("work", "edition", "Title",
            ["Kemp, Kristen", "Lannert, Stacy"], publisher: "Publisher", authorOrderKnown: false);

        EditionMetadataAuthorWritePolicy.TryPrepare(book, legacy, out IReadOnlyList<string> prepared).Should().BeTrue();
        prepared.Should().Equal("Stacy Lannert", "Kristen Kemp");
        EditionMetadataApplicationPolicy.Preview(book, legacy, true).Authors.Should().Equal(prepared);
        legacy.AuthorOrderKnown.Should().BeFalse();
        legacy.Authors.Should().Equal("Kemp, Kristen", "Lannert, Stacy");
    }

    [Fact]
    public void LegacyFactsRemainUsableWithoutGuessingMissingAuthorOrderOrDroppingCoauthors()
    {
        CalibreBook book = new(new(1), "Title", "Unknown", [new(new(1), "Unknown", "Unknown")],
            [], [], "Author/Title (1)");
        EditionMetadataCandidate legacy = new("work", "edition", "Title",
            ["Kemp, Kristen", "Lannert, Stacy"], publisher: "Publisher", authorOrderKnown: false);

        EditionMetadataAuthorWritePolicy.TryPrepare(book, legacy, out IReadOnlyList<string> prepared).Should().BeFalse();
        prepared.Should().BeEmpty();
        EditionMetadataApplicationPolicy.ApplicableFields(book, legacy).Should().Equal(LibraryMetadataField.Publisher);
        EditionMetadataMergePreview preview = EditionMetadataApplicationPolicy.Preview(book, legacy, true);
        preview.Authors.Should().Equal("Unknown");
        preview.Publisher.Should().Be("Publisher");
    }

    [Fact]
    public void LegacyOrderAlignmentCannotMatchTwoCurrentCreditsToOnlyOneProviderCredit()
    {
        CalibreBook book = new(new(1), "Title", "Source",
            [new(new(1), "Alice Writer", "Source"), new(new(2), "alice writer", "Source")],
            [], [], "Author/Title (1)");
        EditionMetadataCandidate legacy = new("work", "edition", "Title",
            ["Alice Writer", "Bob Writer"], authorOrderKnown: false);

        EditionMetadataAuthorWritePolicy.TryPrepare(book, legacy, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("A.C. Baantjer")]
    [InlineData("Baantjer, A.C.")]
    public void CanonicalCurrentInitialSpacingCannotBeCompactedByProviderMetadata(string providerAuthor)
    {
        CalibreBook book = new(new(1), "Title", "Baantjer, A. C.",
            [new(new(1), "A. C. Baantjer", "Baantjer, A. C.")], [], [], "Author/Title (1)");
        EditionMetadataCandidate candidate = new("work", "edition", "Title", [providerAuthor]);

        EditionMetadataApplicationPolicy.ApplicableFields(book, candidate).Should().BeEmpty();
        EditionMetadataApplicationPolicy.Preview(book, candidate, true).Authors.Should().Equal("A. C. Baantjer");
        EditionMetadataApplicationPolicy.Preview(book, candidate, false).Authors.Should().Equal("A. C. Baantjer");
        book.Authors[0].SortName.Should().Be("Baantjer, A. C.");
    }

    [Theory]
    [InlineData("Henry, Patrick")]
    [InlineData("Henry| Patrick")]
    public void MetadataAuthorPreviewUsesDisplayOrderAndConvergesAfterNormalization(string providerAuthor)
    {
        CalibreBook pipe = new(new(1), "Title", "Henry, Patrick",
            [new(new(1), "Henry| Patrick", "Henry, Patrick")], [], [], "Author/Title (1)");
        EditionMetadataCandidate candidate = new("work", "edition", "Title", [providerAuthor]);

        EditionMetadataApplicationPolicy.Preview(pipe, candidate, true).Authors.Should().Equal("Patrick Henry");
        EditionMetadataApplicationPolicy.Preview(pipe, candidate, false).Authors.Should().Equal("Henry| Patrick");
        EditionMetadataApplicationPolicy.CanApplyAutomatically(pipe, candidate).Should().BeFalse();
        CalibreBook corrected = new(pipe.Id, pipe.Title, pipe.AuthorSort,
            [new(new(1), "Patrick Henry", "Henry, Patrick")], [], [], pipe.RelativeDirectory);
        EditionMetadataApplicationPolicy.ApplicableFields(corrected, candidate).Should().BeEmpty();
        EditionMetadataApplicationPolicy.Preview(corrected, candidate, true).Authors.Should().Equal("Patrick Henry");
    }

    [Fact]
    public void PipePairCaseImprovementCannotAutomaticallyReorderOrSplitTheAuthorIdentity()
    {
        CalibreBook book = new(new(1), "Title", "LANNERT, STACY & KEMP, KRISTEN",
            [new(new(1), "LANNERT| STACY| KEMP| KRISTEN", "LANNERT, STACY & KEMP, KRISTEN")],
            [], [], "Author/Title (1)");
        EditionMetadataCandidate candidate = new(
            "work", "edition", "Title", ["Lannert| Stacy| Kemp| Kristen"]);

        EditionMetadataApplicationPolicy.Preview(book, candidate, true).Authors
            .Should().Equal("Stacy Lannert", "Kristen Kemp");
        EditionMetadataApplicationPolicy.CanApplyAutomatically(book, candidate).Should().BeFalse();
    }

    [Theory]
    [InlineData("Victor Victor Appleton", "Victor [pseud. ] Victor [pseud.] Appleton")]
    [InlineData("Sam Christer", "Christer, Sam psued. van Michael Morley")]
    public void CheckedMetadataPreservesCleanedAuthorsAndStillAppliesOtherFields(
        string currentAuthor, string providerAuthor)
    {
        CalibreBook book = new(new(1), "Title", currentAuthor,
            [new(new(1), currentAuthor, currentAuthor)], [], [], "Author/Title (1)");
        EditionMetadataCandidate candidate = new(
            "work", "edition", "Title", [providerAuthor], publisher: "Publisher");

        EditionMetadataApplicationPolicy.ApplicableFields(book, candidate)
            .Should().Equal(LibraryMetadataField.Publisher);
        EditionMetadataApplicationPolicy.Preview(book, candidate, true).Authors.Should().Equal(currentAuthor);
        EditionMetadataApplicationPolicy.Preview(book, candidate, true).Publisher.Should().Be("Publisher");
        candidate.Authors.Should().Equal([providerAuthor], "raw provider provenance must not be rewritten");
    }

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
