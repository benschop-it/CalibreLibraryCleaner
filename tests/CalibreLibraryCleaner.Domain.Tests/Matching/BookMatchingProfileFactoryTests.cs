using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Matching;

public sealed class BookMatchingProfileFactoryTests
{
    [Fact]
    public void ProfileMergesCatalogAndEmbeddedMetadataWithoutReadingContent()
    {
        CalibreBook book = Book(
            1,
            "Imported File 0042",
            [new("isbn", "978-0-306-40615-7")],
            languages: ["eng"]);
        EpubAssessment assessment = Assessment(
            1,
            embeddedTitle: "The Hidden Clock Tower",
            authors: ["Example, Alice"],
            languages: ["nld"],
            identifiers: ["urn:uuid:106a4d0c-6c49-4a1c-a365-5d41f4942b9c"]);

        BookMatchingProfile profile = BookMatchingProfileFactory.Create([book], [assessment]).Single();

        profile.TitleKeys.Should().Contain("THE HIDDEN CLOCK TOWER");
        profile.AuthorKeys.Should().Contain("FAMILY:EXAMPLE");
        profile.Languages.Should().Equal("en", "nl");
        profile.StrongIdentifiers.Should().ContainSingle().Which.Should().Be("ISBN:9780306406157");
        profile.EmbeddedIdentifiers.Should().ContainSingle().Which.Should()
            .Be("UUID:106a4d0c-6c49-4a1c-a365-5d41f4942b9c");
    }

    [Fact]
    public void MatchingFingerprintsBecomeExactBinaryAnchors()
    {
        FormatFileFingerprint fingerprint = new(42, new(new string('a', 64)));
        CalibreBook first = Book(1, "First Catalog Title", formats: [Format(fingerprint, "First.epub")]);
        CalibreBook second = Book(2, "Second Catalog Title", formats: [Format(fingerprint, "Second.epub")]);

        IReadOnlyList<BookMatchingProfile> profiles = BookMatchingProfileFactory.Create([second, first]);
        BookCandidatePair pair = BookCandidateGenerator.Generate(profiles).Pairs.Single();

        profiles.Select(value => value.BookId.Value).Should().Equal(1, 2);
        pair.HasAnchor.Should().BeTrue();
        pair.Evidence.Should().Contain(value => value.Code == "MATCH.BINARY.EXACT");
    }

    [Fact]
    public void InvalidCatalogAndEmbeddedIdentifiersDoNotBecomeEvidence()
    {
        CalibreBook book = Book(1, "Usable Title", [new("isbn", "not-an-isbn")]);
        EpubAssessment assessment = Assessment(1, identifiers: ["not-a-global-id"]);

        BookMatchingProfile profile = BookMatchingProfileFactory.Create([book], [assessment]).Single();

        profile.StrongIdentifiers.Should().BeEmpty();
        profile.EmbeddedIdentifiers.Should().BeEmpty();
    }

    [Fact]
    public void UnusableTitlesAreSkippedAndCancellationIsObserved()
    {
        CalibreBook unusable = Book(1, "---");
        using CancellationTokenSource source = new();
        source.Cancel();

        BookMatchingProfileFactory.Create([unusable]).Should().BeEmpty();
        FluentActions.Invoking(() => BookMatchingProfileFactory.Create([unusable], cancellationToken: source.Token))
            .Should().Throw<OperationCanceledException>();
    }

    private static CalibreBook Book(
        long id,
        string title,
        BookIdentifier[]? identifiers = null,
        string[]? languages = null,
        BookFormat[]? formats = null) => new(
        new(id),
        title,
        "Example, Alice",
        [new(new(id), "Alice Example", "Example, Alice")],
        identifiers ?? [],
        formats ?? [],
        $"Author/Book {id}",
        new(languages: languages));

    private static BookFormat Format(FormatFileFingerprint fingerprint, string name) => new(
        "EPUB",
        Path.GetFileNameWithoutExtension(name),
        "Author/" + name,
        FormatFileStatus.Present,
        fingerprint,
        new(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));

    private static EpubAssessment Assessment(
        long id,
        string? embeddedTitle = null,
        string[]? authors = null,
        string[]? languages = null,
        string[]? identifiers = null) => new(
        new(id),
        "EPUB",
        $"Author/Book {id}.epub",
        null,
        AssessmentStatus.Unassessed,
        null,
        new("epub-inspector/1.0.4"),
        new("epub-quality/1.0.3"),
        new(true, true, embeddedTitle: embeddedTitle, authors: authors,
            languages: languages, strongIdentifiers: identifiers),
        [new("EPUB.TEST.UNASSESSED", FindingSeverity.Information, 0, "Synthetic unassessed result.")]);
}
