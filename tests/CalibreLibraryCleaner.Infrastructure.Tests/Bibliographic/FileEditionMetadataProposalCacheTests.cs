using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Infrastructure.Bibliographic;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Bibliographic;

public sealed class FileEditionMetadataProposalCacheTests
{
    private static readonly EditionMetadataProviderIdentity Provider = new(
        "open-library", "open-library-edition-search/1.0.0");
    private static readonly DateTimeOffset RetrievedAt = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RoundTripUsesOpaqueKeyAndStoresNormalizedProposalWithoutRawQuery()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = Query();
        EditionMetadataProposal proposal = Proposal(query);

        await cache.WriteAsync(query, proposal, CancellationToken.None);
        EditionMetadataProposal? loaded = await cache.TryReadAsync(
            Query(new(2)), RetrievedAt.AddDays(-1), CancellationToken.None);

        loaded!.BookId.Should().Be(new CalibreBookId(2));
        loaded.Candidate.Should().BeEquivalentTo(proposal.Candidate);
        string path = Directory.GetFiles(directory.Path, "*.json").Single();
        Path.GetFileNameWithoutExtension(path).Should().MatchRegex("^[0-9a-f]{64}$");
        string json = await File.ReadAllTextAsync(path);
        json.Should().NotContain("Private Query Title");
        json.Should().NotContain("Private Query Author");
        json.Should().Contain("Canonical Online Title");
        json.Should().NotContain(directory.Path);
    }

    [Fact]
    public async Task StaleCorruptAndPolicyMismatchEntriesBecomeMisses()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = Query();
        await cache.WriteAsync(query, Proposal(query), CancellationToken.None);
        string path = Directory.GetFiles(directory.Path, "*.json").Single();

        (await cache.TryReadAsync(query, RetrievedAt.AddMinutes(1), CancellationToken.None))
            .Should().BeNull();
        string json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace(
            EditionMetadataProposal.PolicyVersion,
            "edition-metadata-proposal/0.0.0",
            StringComparison.Ordinal));
        (await cache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None))
            .Should().BeNull();
        await File.WriteAllTextAsync(path, "{");
        (await cache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None))
            .Should().BeNull();
    }

    private static EditionMetadataLookupQuery Query(CalibreBookId? bookId = null) => new(
        bookId ?? new(1),
        Provider,
        EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors
            | EditionMetadataQueryFields.Language,
        null,
        "Private Query Title",
        ["Private Query Author"],
        "en");

    private static EditionMetadataProposal Proposal(EditionMetadataLookupQuery query) => new(
        query.BookId,
        Provider,
        query.Fields,
        RetrievedAt,
        EditionMetadataProposalStatus.Proposed,
        new(
            "/works/OL66554W",
            "/books/OL7353617M",
            "Canonical Online Title",
            ["Canonical Online Author"],
            [new("isbn", "9780141439518")],
            "Canonical Publisher",
            new(2003, 1, 29),
            ["en"],
            "Canonical Series",
            cover: new("8225261", "https://covers.openlibrary.org/b/id/8225261-L.jpg")),
        4_208,
        ["METADATA.EDITION.AUTHOR_COMPATIBLE", "METADATA.EDITION.TITLE_EXACT"]);
}
