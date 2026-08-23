using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class EditionMetadataProposalTests
{
    private static readonly JsonSerializerOptions FixtureJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly EditionMetadataProviderIdentity Provider = new(
        "open-library", "open-library-edition-search/1.0.0");
    private static readonly DateTimeOffset RetrievedAt = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LabeledFixtureSelectsOnlyOneCoherentEditionOrRemainsAmbiguous()
    {
        Fixture fixture = LoadFixture();

        foreach (Scenario scenario in fixture.Scenarios)
        {
            EditionMetadataLookupQuery query = Query(scenario.Query);
            EditionMetadataSearchResult search = new(
                Provider,
                EditionMetadataSearchStatus.Success,
                RetrievedAt,
                scenario.Candidates.Select(Candidate));

            EditionMetadataProposal proposal = EditionMetadataProposalPolicy.Resolve(query, search);

            proposal.Status.ToString().Should().Be(scenario.ExpectedStatus, scenario.Id);
            proposal.Candidate?.EditionId.Should().Be(scenario.ExpectedEditionId, scenario.Id);
        }
    }

    [Fact]
    public void QueryCreationPrefersValidatedIsbnAndDisclosesOnlyIdentifier()
    {
        CalibreBook book = new(
            new(1),
            "Private Title",
            "Author, Private",
            [new(new(1), "Private Author", "Author, Private")],
            [new("isbn", "9780141439518")],
            [],
            "Private/Path");

        EditionMetadataLookupQuery query = EditionMetadataLookupQuery.Create(book, Provider);

        query.Fields.Should().Be(EditionMetadataQueryFields.Identifier);
        query.Identifier.Should().Be("ISBN:9780141439518");
        query.Title.Should().BeNull();
        query.Authors.Should().BeEmpty();
    }

    [Fact]
    public void QueryVariantsAreBoundedFromStrictIdentityToBroaderRetrieval()
    {
        CalibreBook book = new(
            new(1),
            "Book Title: A Subtitle",
            "Primary, Author & Secondary, Author",
            [
                new(new(1), "Primary Author", "Primary, Author"),
                new(new(2), "Secondary Author", "Secondary, Author"),
            ],
            [new("isbn", "9780141439518")],
            [],
            "Author/Book",
            new(languages: ["eng"]));

        IReadOnlyList<EditionMetadataLookupQuery> variants =
            EditionMetadataLookupQuery.CreateVariants(book, Provider);
        EditionMetadataLookupQuery validation =
            EditionMetadataLookupQuery.CreateValidation(book, Provider);

        variants.Should().HaveCount(5);
        variants[0].Fields.Should().Be(EditionMetadataQueryFields.Identifier);
        variants[1].Language.Should().Be("en");
        variants[2].Language.Should().BeNull();
        variants[3].Authors.Should().ContainSingle("Primary Author");
        variants[4].Title.Should().Be("Book Title");
        validation.Fields.Should().HaveFlag(EditionMetadataQueryFields.Title)
            .And.HaveFlag(EditionMetadataQueryFields.Authors)
            .And.HaveFlag(EditionMetadataQueryFields.Language);
        validation.Fields.Should().NotHaveFlag(EditionMetadataQueryFields.Identifier);
        validation.Title.Should().Be("Book Title: A Subtitle");
        EditionMetadataLookupQuery.PolicyVersion.Should().Be("edition-metadata-query/1.4.0");
    }

    [Fact]
    public void ProposalRetainsBoundedProviderAndSelectionProvenance()
    {
        EditionMetadataLookupQuery query = new(
            new(1),
            Provider,
            EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors,
            null,
            "Pride and Prejudice",
            ["Jane Austen"],
            null);
        EditionMetadataCandidate candidate = new(
            "/works/OL66554W",
            "/books/OL7353617M",
            "Pride and Prejudice",
            ["Jane Austen"],
            [new("isbn", "9780141439518")]);

        EditionMetadataProposal proposal = EditionMetadataProposalPolicy.Resolve(
            query,
            new(Provider, EditionMetadataSearchStatus.Success, RetrievedAt, [candidate]));

        proposal.Provider.Should().Be(Provider);
        proposal.QueryFields.Should().Be(query.Fields);
        proposal.RetrievedAtUtc.Should().Be(RetrievedAt);
        proposal.MatchScore.Should().BePositive();
        proposal.ReasonCodes.Should().Contain("METADATA.EDITION.TITLE_EXACT");
        proposal.Candidate.Should().BeSameAs(candidate);
    }

    [Fact]
    public void DifferentEditionWithCoreTitleAndExactAuthorProducesProposal()
    {
        EditionMetadataLookupQuery query = new(
            new(1),
            Provider,
            EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors,
            null,
            "Quicksilver (Baroque Cycle, #1)",
            ["Neal Stephenson"],
            null);
        EditionMetadataCandidate candidate = new(
            "/works/OL18199543W",
            "/books/OL7282656M",
            "Quicksilver",
            ["Neal Stephenson"],
            [new("isbn", "9780060833169")]);

        EditionMetadataProposal proposal = EditionMetadataProposalPolicy.Resolve(
            query,
            new(Provider, EditionMetadataSearchStatus.Success, RetrievedAt, [candidate]));

        proposal.Status.Should().Be(EditionMetadataProposalStatus.Proposed);
        proposal.ReasonCodes.Should().Contain("METADATA.EDITION.TITLE_EXACT")
            .And.Contain("METADATA.EDITION.AUTHOR_EXACT");
    }

    private static EditionMetadataLookupQuery Query(QueryDocument value)
    {
        if (value.Identifier is not null)
            return new(new(1), Provider, EditionMetadataQueryFields.Identifier,
                value.Identifier, null, null, null);
        EditionMetadataQueryFields fields = EditionMetadataQueryFields.Title
            | EditionMetadataQueryFields.Authors;
        if (value.Language is not null) fields |= EditionMetadataQueryFields.Language;
        return new(new(1), Provider, fields, null, value.Title, value.Authors, value.Language);
    }

    private static EditionMetadataCandidate Candidate(CandidateDocument value) => new(
        value.WorkId,
        value.EditionId,
        value.Title,
        value.Authors,
        value.Isbns.Select(isbn => new EditionMetadataIdentifier("isbn", isbn)),
        value.Publisher,
        new(value.Year),
        [value.Language]);

    private static Fixture LoadFixture()
    {
        Assembly assembly = typeof(EditionMetadataProposalTests).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(value =>
            value.EndsWith("edition-metadata.v1.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Edition metadata fixture is unavailable.");
        return JsonSerializer.Deserialize<Fixture>(stream, FixtureJsonOptions)
            ?? throw new InvalidDataException("Edition metadata fixture is empty.");
    }

    private sealed record Fixture(string SchemaVersion, Scenario[] Scenarios);
    private sealed record Scenario(
        string Id,
        QueryDocument Query,
        CandidateDocument[] Candidates,
        string ExpectedStatus,
        string? ExpectedEditionId);
    private sealed record QueryDocument(
        string? Identifier,
        string? Title,
        string[]? Authors,
        string? Language);
    private sealed record CandidateDocument(
        string WorkId,
        string EditionId,
        string Title,
        string[] Authors,
        string[] Isbns,
        string? Publisher,
        int Year,
        string Language);
}
