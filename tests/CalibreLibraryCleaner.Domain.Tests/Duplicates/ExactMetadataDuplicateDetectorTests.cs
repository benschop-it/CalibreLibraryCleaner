using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Duplicates;

public sealed class ExactMetadataDuplicateDetectorTests
{
    [Fact]
    public void DetectGroupsExactNormalizedTitleAndAuthorSet()
    {
        CalibreBook[] books =
        [
            CreateBook(3, "Other", ["Alice Smith"]),
            CreateBook(2, "The  Book : A Tale", ["Bob Jones", "Alice Smith"]),
            CreateBook(1, "the book: a tale", ["Alice Smith", "Bob Jones"]),
        ];

        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();

        group.Members.Select(member => member.Value).Should().Equal(1, 2);
        group.Identity.Title.Value.Should().Be("THE BOOK:A TALE");
        group.Identity.Authors.Names.Select(name => name.Value).Should().Equal("ALICE SMITH", "BOB JONES");
        group.MatchReason.Code.Should().Be("EXACT_NORMALIZED_TITLE_AUTHOR_SET");
        group.MatchReason.Category.Should().Be("Exact normalized metadata candidate");
    }

    [Fact]
    public void AuthorSubsetDoesNotMatchAllAuthors()
    {
        CalibreBook[] books =
        [
            CreateBook(1, "Book", ["Main Author"]),
            CreateBook(2, "Book", ["Main Author", "Second Author"]),
        ];

        ExactMetadataDuplicateDetector.Detect(books).Should().BeEmpty();
    }

    [Fact]
    public void AuthorSortInvalidIdentifiersAndFingerprintsDoNotAffectMetadataIdentity()
    {
        CalibreBook first = CreateBook(
            1,
            "Book",
            ["Jane Doe"],
            authorSort: "Doe, Jane",
            identifier: "one",
            digestCharacter: 'a');
        CalibreBook second = CreateBook(
            2,
            "BOOK",
            ["Jane Doe"],
            authorSort: "Different",
            identifier: "two",
            digestCharacter: 'b');

        ExactMetadataDuplicateDetector.Detect([first, second]).Should().ContainSingle();
    }

    [Fact]
    public void DisjointKnownLanguagesSuppressAmbiguousExactGroup()
    {
        CalibreBook first = CreateBook(1, "Book", ["Jane Doe"], languages: ["eng"]);
        CalibreBook second = CreateBook(2, "Book", ["Jane Doe"], languages: ["nld"]);

        ExactMetadataDuplicateDetector.Detect([first, second]).Should().BeEmpty();
    }

    [Fact]
    public void LanguageAliasesAndUnknownLanguageRemainCompatible()
    {
        CalibreBook english = CreateBook(1, "Book", ["Jane Doe"], languages: ["eng"]);
        CalibreBook alias = CreateBook(2, "Book", ["Jane Doe"], languages: ["en"]);
        CalibreBook unknown = CreateBook(3, "Book", ["Jane Doe"]);

        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(
            [unknown, alias, english]).Single();

        group.Members.Select(value => value.Value).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void ConflictingValidatedStrongIdentifiersSuppressAmbiguousExactGroup()
    {
        CalibreBook first = CreateBook(
            1, "Book", ["Jane Doe"], identifiers: [new("isbn", "9780306406157")]);
        CalibreBook second = CreateBook(
            2, "Book", ["Jane Doe"], identifiers: [new("isbn", "9780140328721")]);

        ExactMetadataDuplicateDetector.Detect([first, second]).Should().BeEmpty();
    }

    [Fact]
    public void OverlappingStrongIdentifierSetsRemainCompatible()
    {
        CalibreBook first = CreateBook(
            1,
            "Book",
            ["Jane Doe"],
            identifiers: [new("isbn", "9780306406157"), new("isbn", "9780140328721")]);
        CalibreBook second = CreateBook(
            2,
            "Book",
            ["Jane Doe"],
            identifiers: [new("isbn", "9780140328721"), new("isbn", "9780131103627")]);

        ExactMetadataDuplicateDetector.Detect([first, second]).Should().ContainSingle();
    }

    [Fact]
    public void UniqueRepeatedConsensusExcludesDisjointOutlier()
    {
        CalibreBook[] books =
        [
            CreateBook(3, "Book", ["Jane Doe"], languages: ["fr"]),
            CreateBook(1, "Book", ["Jane Doe"], languages: ["en"]),
            CreateBook(2, "Book", ["Jane Doe"], languages: ["eng"]),
        ];

        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();

        group.Members.Select(value => value.Value).Should().Equal(1, 2);
    }

    [Fact]
    public void TiedRepeatedConflictingSignalsSuppressIdentityBucket()
    {
        CalibreBook[] books =
        [
            CreateBook(1, "Book", ["Jane Doe"], languages: ["en"]),
            CreateBook(2, "Book", ["Jane Doe"], languages: ["eng"]),
            CreateBook(3, "Book", ["Jane Doe"], languages: ["fr"]),
            CreateBook(4, "Book", ["Jane Doe"], languages: ["fra"]),
        ];

        ExactMetadataDuplicateDetector.Detect(books).Should().BeEmpty();
    }

    [Fact]
    public void LanguageAndIdentifierOutliersAreUnionedDeterministically()
    {
        BookIdentifier isbnA = new("isbn", "9780306406157");
        BookIdentifier isbnB = new("isbn", "9780140328721");
        CalibreBook[] books =
        [
            CreateBook(4, "Book", ["Jane Doe"], languages: ["en"], identifiers: [isbnB]),
            CreateBook(3, "Book", ["Jane Doe"], languages: ["fr"], identifiers: [isbnA]),
            CreateBook(2, "Book", ["Jane Doe"], languages: ["eng"], identifiers: [isbnA]),
            CreateBook(1, "Book", ["Jane Doe"], languages: ["en"], identifiers: [isbnA]),
        ];

        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();

        group.Members.Select(value => value.Value).Should().Equal(1, 2);
        ExactMetadataDuplicateDetector.Detect(books.Reverse()).Single().Members.Should().Equal(group.Members);
    }

    [Fact]
    public void MissingAuthorsAndEmptyNormalizedAuthorsNeverFallBackToTitleOnly()
    {
        CalibreBook[] books =
        [
            CreateBook(1, "Book", []),
            CreateBook(2, "Book", ["\u200b"]),
            CreateBook(3, "Book", ["Author"]),
            CreateBook(4, "Book", ["Author", "\u200b"]),
        ];

        ExactMetadataDuplicateDetector.Detect(books).Should().BeEmpty();
    }

    [Fact]
    public void GroupsAndIdsAreDeterministicForShuffledInput()
    {
        CalibreBook[] books =
        [
            CreateBook(4, "Zulu", ["Author"]),
            CreateBook(2, "Alpha", ["Second", "First"]),
            CreateBook(3, "Zulu", ["Author"]),
            CreateBook(1, "Alpha", ["First", "Second"]),
        ];

        IReadOnlyList<ExactMetadataDuplicateGroup> forward = ExactMetadataDuplicateDetector.Detect(books);
        IReadOnlyList<ExactMetadataDuplicateGroup> reverse = ExactMetadataDuplicateDetector.Detect(books.Reverse());

        forward.Select(group => group.Identity.Title.Value).Should().Equal("ALPHA", "ZULU");
        reverse.Select(group => group.Id).Should().Equal(forward.Select(group => group.Id));
        reverse.SelectMany(group => group.Members).Should().Equal(forward.SelectMany(group => group.Members));
        forward[0].Id.Value.Should().StartWith("exact-metadata:v1|title|");
    }

    [Fact]
    public void GroupRequiresTwoDistinctRecords()
    {
        MetadataTextNormalizer.TryNormalizeTitle("Book", out NormalizedTitle? title);
        MetadataTextNormalizer.TryCreateAuthorSet(["Author"], out NormalizedAuthorSet? authors);

        Action act = () => _ = new ExactMetadataDuplicateGroup(
            new NormalizedBookIdentity(title!, authors!),
            [new CalibreBookId(1), new CalibreBookId(1)]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DetectObservesCancellationWhileEnumeratingBooks()
    {
        using CancellationTokenSource cancellation = new();

        Action act = () => ExactMetadataDuplicateDetector.Detect(Books(), cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
        return;

        IEnumerable<CalibreBook> Books()
        {
            yield return CreateBook(1, "Book", ["Author"]);
            cancellation.Cancel();
            yield return CreateBook(2, "Book", ["Author"]);
        }
    }

    [Fact]
    public void DetectScalesToFiftyThousandRecordsWithDeterministicOutput()
    {
        CalibreBook[] books = Enumerable.Range(1, 50_000)
            .Select(index => CreateBook(
                index,
                $"Book {(index - 1) / 2:D5}",
                index % 2 == 0 ? ["First Author", "Second Author"] : ["Second Author", "First Author"]))
            .OrderBy(book => (book.Id.Value * 7919) % 50_000)
            .ToArray();

        IReadOnlyList<ExactMetadataDuplicateGroup> groups = ExactMetadataDuplicateDetector.Detect(books);

        groups.Should().HaveCount(25_000);
        groups.SelectMany(group => group.Members).Should().HaveCount(50_000);
        groups.Should().OnlyContain(group => group.Members.Count == 2);
        groups.Select(group => group.Identity.Title.Value).Should().Equal(
            groups.Select(group => group.Identity.Title.Value).OrderBy(value => value, StringComparer.Ordinal));
        groups[0].Identity.Title.Value.Should().Be("BOOK 00000");
        groups[^1].Identity.Title.Value.Should().Be("BOOK 24999");
        groups.Should().OnlyContain(group => group.Identity.Authors.Names.Count == 2);
    }

    private static CalibreBook CreateBook(
        int id,
        string title,
        IReadOnlyList<string> authors,
        string authorSort = "Stored sort",
        string identifier = "identifier",
        char digestCharacter = 'a',
        IReadOnlyList<string>? languages = null,
        IReadOnlyList<BookIdentifier>? identifiers = null) => new(
        new CalibreBookId(id),
        title,
        authorSort,
        authors.Select((author, index) => new BookAuthor(
            new CalibreAuthorId((id * 10L) + index + 1),
            author,
            $"Sort {index}")),
        identifiers ?? [new BookIdentifier("test", identifier)],
        [new BookFormat(
            "EPUB",
            $"Book {id}",
            $"Book {id}/Book.epub",
            FormatFileStatus.Present,
            new FormatFileFingerprint(id, new Sha256Digest(new string(digestCharacter, 64))),
            new FormatFileObservation(id, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))],
        $"Book {id}",
        new(languages: languages));
}
