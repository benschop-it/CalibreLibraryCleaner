using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Matching;

public sealed class BookCandidateGeneratorTests
{
    [Fact]
    public void KnownTranslationLanguageConflictSkipsContentThatCannotAffectGrouping()
    {
        BookMatchingProfile english = Profile(
            1,
            "The Clockmaker and the Hidden Tower",
            ["A. B. Example"],
            languages: ["eng"]);
        BookMatchingProfile dutch = Profile(
            2,
            "De Klokkenmaker en de Verborgen Toren",
            ["Example, A.B."],
            languages: ["nld"]);

        BookCandidateGenerator.Generate([english, dutch]).Pairs.Should().BeEmpty();
    }

    [Fact]
    public void ExactValidatedIdentifierAnchorsDifferentMetadata()
    {
        BookMatchingProfile first = Profile(
            1, "Converted Document", ["J. K. Example"], strongIdentifiers: ["ISBN:9780306406157"]);
        BookMatchingProfile second = Profile(
            2, "A Completely Different Catalog Title", ["Example, Joanne K."],
            strongIdentifiers: ["ISBN:9780306406157"]);

        BookCandidatePair pair = BookCandidateGenerator.Generate([first, second]).Pairs.Single();

        pair.HasAnchor.Should().BeTrue();
        pair.NeedsContentEvidence.Should().BeTrue();
        pair.Evidence.Should().Contain(value => value.Code == "MATCH.IDENTIFIER.EXACT");
    }

    [Fact]
    public void SameAuthorAloneDoesNotRequestContentAcrossDifferentWorks()
    {
        BookMatchingProfile first = Profile(1, "The Northern Observatory", ["Alice Example"], ["eng"]);
        BookMatchingProfile second = Profile(2, "Gardens Beneath Glass", ["Alice Example"], ["eng"]);

        BookCandidateGenerator.Generate([first, second]).Pairs.Should().BeEmpty();
    }

    [Fact]
    public void SimilarTitleAndAuthorRequestContentForAmbiguousWork()
    {
        BookMatchingProfile first = Profile(1, "The Northern Observatory", ["Alice Example"], ["eng"]);
        BookMatchingProfile second = Profile(2, "Northern Observatory Illustrated", ["Alice Example"], ["eng"]);

        BookCandidatePair pair = BookCandidateGenerator.Generate([first, second]).Pairs.Single();

        pair.Evidence.Should().Contain(value => value.Code == "MATCH.TITLE.TOKEN_MEDIUM");
        pair.NeedsContentEvidence.Should().BeTrue();
    }

    [Fact]
    public void OrdinaryCandidatesAreCappedDeterministicallyPerRecord()
    {
        BookMatchingProfile seed = Profile(1, "Shared Subject Alpha", ["Common Author"]);
        BookMatchingProfile[] candidates = Enumerable.Range(2, 50)
            .Select(id => Profile(id, $"Shared Subject {id:D2}", ["Common Author"]))
            .ToArray();

        BookCandidateGenerationResult forward = BookCandidateGenerator.Generate(
            [seed, .. candidates], new(maximumCandidatesPerRecord: 20));
        BookCandidateGenerationResult reverse = BookCandidateGenerator.Generate(
            candidates.Reverse().Append(seed), new(maximumCandidatesPerRecord: 20));

        forward.RecordsCapped.Should().BeGreaterThan(0);
        forward.Pairs.Where(value => value.Id.First == seed.BookId || value.Id.Second == seed.BookId)
            .Should().HaveCount(20);
        forward.Pairs.SelectMany(value => new[] { value.Id.First, value.Id.Second })
            .GroupBy(value => value)
            .Should().OnlyContain(group => group.Count() <= 20);
        reverse.Pairs.Select(value => value.Id).Should().Equal(forward.Pairs.Select(value => value.Id));
    }

    [Fact]
    public void DecisiveAnchorsArePreservedBeyondOrdinaryCap()
    {
        string[] keys = Enumerable.Range(1, 25).Select(value => $"HASH:{value:D2}").ToArray();
        BookMatchingProfile seed = Profile(1, "Seed Work", ["Author"], exactBinaryKeys: keys);
        BookMatchingProfile[] anchors = keys.Select((key, index) =>
            Profile(index + 2, $"UniqueToken{index:D2}", ["Author"], exactBinaryKeys: [key])).ToArray();

        BookCandidateGenerationResult result = BookCandidateGenerator.Generate(
            [seed, .. anchors], new(maximumCandidatesPerRecord: 20));

        result.LimitExceeded.Should().BeFalse();
        result.Pairs.Should().HaveCount(25);
        result.Pairs.Should().OnlyContain(value => value.HasAnchor);
    }

    [Fact]
    public void OversizedOrdinaryBucketsDoNotCreateAllPairs()
    {
        BookMatchingProfile[] profiles = Enumerable.Range(1, 500)
            .Select(id => Profile(id, $"Common Token Work {id:D4}", ["Popular Author"]))
            .ToArray();

        BookCandidateGenerationResult result = BookCandidateGenerator.Generate(
            profiles, new(maximumOrdinaryBucketSize: 32));

        result.MaximumObservedBucketSize.Should().Be(500);
        result.Pairs.Should().BeEmpty();
        result.LimitExceeded.Should().BeFalse();
    }

    [Fact]
    public void NormalizationIsGenericAndLanguageCodesConverge()
    {
        CandidateMetadataNormalizer.AuthorKeys(["Surname, J. K."])
            .Should().Contain("FAMILY:SURNAME");
        CandidateMetadataNormalizer.AuthorKeys(["J K Surname"])
            .Should().Contain("FAMILY:SURNAME");
        CandidateMetadataNormalizer.AuthorKeys(["Unknown"]).Should().BeEmpty();
        CandidateMetadataNormalizer.NormalizeLanguage("nld").Should().Be("nl");
        CandidateMetadataNormalizer.NormalizeLanguage("eng").Should().Be("en");
        CandidateMetadataNormalizer.TitleKeys("03 Hidden Tower")
            .Should().Contain("HIDDEN TOWER");
    }

    [Fact]
    public void InitialSpacingFullNamesAndCommaOrderShareOneAuthorIdentity()
    {
        string[][] variants =
        [
            ["J. K. Rowling"],
            ["J.K. Rowling"],
            ["J.K.Rowling"],
            ["Joanne K. Rowling"],
            ["Joanne Kathleen Rowling"],
            ["Rowling, J. K_"],
            ["Rowling, J.K_"],
        ];
        string[][] keys = variants.Select(CandidateMetadataNormalizer.AuthorKeys).ToArray();

        keys.Select(CandidateMetadataNormalizer.AuthorAliasKeys)
            .Should().OnlyContain(value => value.Contains("ROWLING|JK"));
        foreach (string[] first in keys)
            foreach (string[] second in keys)
                CandidateMetadataNormalizer.HaveCompatibleAuthorIdentity(first, second).Should().BeTrue();
    }

    [Fact]
    public void SameInitialsDoNotMergeConflictingExpandedGivenNames()
    {
        string[] joanne = CandidateMetadataNormalizer.AuthorKeys(["Joanne Kathleen Rowling"]);
        string[] john = CandidateMetadataNormalizer.AuthorKeys(["John Kevin Rowling"]);
        string[] initials = CandidateMetadataNormalizer.AuthorKeys(["J. K. Rowling"]);

        CandidateMetadataNormalizer.HaveCompatibleAuthorIdentity(joanne, john).Should().BeFalse();
        CandidateMetadataNormalizer.HaveCompatibleAuthorIdentity(initials, joanne).Should().BeTrue();
        CandidateMetadataNormalizer.HaveCompatibleAuthorIdentity(initials, john).Should().BeTrue();
    }

    [Fact]
    public void SharedSurnameWithDifferentInitialsDoesNotCreateAuthorGroup()
    {
        BookMatchingProfile alice = Profile(1, "Shared Work", ["Alice Smith"], ["eng"]);
        BookMatchingProfile bob = Profile(2, "Shared Work", ["Bob Smith"], ["eng"]);

        BookCandidateGenerator.Generate([alice, bob]).Pairs.Should().BeEmpty();
    }

    [Theory]
    [InlineData("isbn", "978-0-306-40615-7", "ISBN:9780306406157")]
    [InlineData("doi", "https://doi.org/10.1000/182", "DOI:10.1000/182")]
    [InlineData("asin", "b012345678", "ASIN:B012345678")]
    [InlineData("oclc", "ocn00012345", "OCLC:12345")]
    public void StrongIdentifiersRequireTypeSpecificValidation(string type, string value, string expected) =>
        CandidateMetadataNormalizer.NormalizeStrongIdentifier(type, value).Should().Be(expected);

    [Theory]
    [InlineData("urn:isbn:978-0-306-40615-7", "ISBN:9780306406157")]
    [InlineData("urn:doi:10.1000/182", "DOI:10.1000/182")]
    [InlineData("urn:uuid:106a4d0c-6c49-4a1c-a365-5d41f4942b9c", "UUID:106a4d0c-6c49-4a1c-a365-5d41f4942b9c")]
    public void EmbeddedIdentifiersRequireRecognizableGlobalSyntax(string value, string expected) =>
        CandidateMetadataNormalizer.NormalizeEmbeddedIdentifier(value).Should().Be(expected);

    [Fact]
    public void CandidateGenerationObservesCancellation()
    {
        using CancellationTokenSource source = new();
        source.Cancel();

        FluentActions.Invoking(() => BookCandidateGenerator.Generate(
                [Profile(1, "Usable Work", ["Author"])], cancellationToken: source.Token))
            .Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void DecisivePairOverflowFailsClosed()
    {
        BookMatchingProfile[] profiles = Enumerable.Range(1, 10)
            .Select(id => Profile(id, $"Work {id}", ["Author"],
                strongIdentifiers: ["ISBN:9780306406157"]))
            .ToArray();

        BookCandidateGenerationResult result = BookCandidateGenerator.Generate(
            profiles, new(maximumCandidatesPerRecord: 20, maximumUniquePairs: 10));

        result.LimitExceeded.Should().BeTrue();
        result.Pairs.Should().BeEmpty();
    }

    private static BookMatchingProfile Profile(
        long id,
        string title,
        string[] authors,
        string[]? languages = null,
        string[]? strongIdentifiers = null,
        string[]? embeddedIdentifiers = null,
        string[]? exactBinaryKeys = null,
        string? series = null,
        decimal? seriesIndex = null) => new(
        new(id),
        CandidateMetadataNormalizer.TitleKeys(title),
        CandidateMetadataNormalizer.TitleTokens(title),
        CandidateMetadataNormalizer.AuthorKeys(authors),
        CandidateMetadataNormalizer.AuthorTokens(authors),
        (languages ?? []).Select(CandidateMetadataNormalizer.NormalizeLanguage).Where(value => value is not null)!,
        strongIdentifiers,
        embeddedIdentifiers,
        exactBinaryKeys,
        CandidateMetadataNormalizer.NormalizeSeries(series),
        seriesIndex);
}
