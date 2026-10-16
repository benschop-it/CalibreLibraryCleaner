using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Infrastructure.Bibliographic;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Bibliographic;

public sealed class FileEditionMetadataProposalCacheTests
{
    private static readonly EditionMetadataProviderIdentity Provider = new(
        "open-library", "open-library-edition-search/1.0.0");
    private static readonly EditionMetadataProviderIdentity CurrentProvider = new(
        "open-library", "open-library-edition-search/1.2.0");
    private static readonly DateTimeOffset RetrievedAt = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DefaultOptionsAllowOneGibibyteOfGlobalEntries()
    {
        EditionMetadataProposalCacheOptions options = new();

        options.MaximumTotalBytes.Should().Be(1024L * 1024 * 1024);
    }

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

    [Fact]
    public async Task CurrentOpenLibraryQueryReusesValidLegacyEntryButRetriesOldExtractionMiss()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = Query(provider: CurrentProvider);
        await cache.WriteAsync(query, CompatibleProposal(query), CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query);

        EditionMetadataProposal? loaded = await cache.TryReadAsync(
            query, RetrievedAt.AddDays(-1), CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Provider.Should().Be(CurrentProvider);

        using TemporaryDirectory missDirectory = new();
        FileEditionMetadataProposalCache missCache = new(new(missDirectory.Path));
        EditionMetadataProposal miss = new(
            query.BookId,
            query.Provider,
            query.Fields,
            RetrievedAt,
            EditionMetadataProposalStatus.NotFound,
            problemCode: "OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS");
        await missCache.WriteAsync(query, miss, CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(missDirectory.Path, query);

        (await missCache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task ExistingProposalBackfillsIdentifierAliasForChangedBookMetadata()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery textQuery = Query(provider: CurrentProvider);
        await cache.WriteAsync(textQuery, Proposal(textQuery), CancellationToken.None);

        await cache.PrepareIdentifierAliasesAsync(
            CurrentProvider, RetrievedAt.AddDays(-1), CancellationToken.None);
        EditionMetadataLookupQuery identifierQuery = new(
            new(99),
            CurrentProvider,
            EditionMetadataQueryFields.Identifier,
            "ISBN:9780141439518",
            null,
            null,
            null);
        EditionMetadataProposal? loaded = await cache.TryReadAsync(
            identifierQuery, RetrievedAt.AddDays(-1), CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.BookId.Should().Be(new CalibreBookId(99));
        loaded.ReasonCodes.Should().Contain("METADATA.EDITION.ISBN_EXACT");
    }

    [Theory]
    [InlineData("open-library", "open-library-edition-search/1.2.0")]
    [InlineData("open-library", "open-library-edition-search/1.1.0")]
    [InlineData("google-books", "google-books-search/1.0.0")]
    public async Task PreviousQueryPolicyReusesCompatibleProposalWithCurrentValidation(string providerId, string providerVersion)
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = Query(provider: new(providerId, providerVersion));
        await cache.WriteAsync(query, CompatibleProposal(query), CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query, "edition-metadata-query/1.4.0", providerVersion);

        EditionMetadataProposal? cold = await cache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None);
        EditionMetadataProposal? warm = await cache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None);

        cold.Should().NotBeNull();
        warm.Should().BeEquivalentTo(cold);
        cold!.Status.Should().Be(EditionMetadataProposalStatus.Proposed);
        cold.Provider.Should().Be(query.Provider);
        cold.RetrievedAtUtc.Should().Be(RetrievedAt);
        cold.ReasonCodes.Should().Contain("METADATA.EDITION.AUTHOR_EXACT");
        cold.MatchScore.Should().NotBe(4_208);
    }

    [Fact]
    public async Task PreviousQueryPolicyDoesNotReuseCandidateRejectedByCurrentPenNameRules()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = new(new(1), CurrentProvider,
            EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors, null,
            "The Book", ["Morgan Vale (pseud. van Alex Writer)"], null);
        EditionMetadataProposal obsolete = new(query.BookId, query.Provider, query.Fields, RetrievedAt,
            EditionMetadataProposalStatus.Proposed,
            new("work", "edition", "The Book", ["Alex Writer"]), 4_208, ["METADATA.EDITION.AUTHOR_COMPATIBLE"]);
        await cache.WriteAsync(query, obsolete, CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query, "edition-metadata-query/1.4.0", CurrentProvider.Version);

        (await cache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("open-library", "open-library-edition-search/1.2.0", null)]
    [InlineData("open-library", "open-library-edition-search/1.2.0", "OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS")]
    [InlineData("google-books", "google-books-volume-search/1.0.0", null)]
    [InlineData("google-books", "google-books-volume-search/1.0.0", "GOOGLE_BOOKS.NO_COMPATIBLE_VOLUMES")]
    public async Task CompatiblePreviousTextAndIdentifierMissesRemainReusable(
        string providerId, string providerVersion, string? problemCode)
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataProviderIdentity provider = new(providerId, providerVersion);
        EditionMetadataLookupQuery textQuery = Query(provider: provider);
        await cache.WriteAsync(textQuery, new(textQuery.BookId, textQuery.Provider, textQuery.Fields,
            RetrievedAt, EditionMetadataProposalStatus.NotFound, problemCode: problemCode), CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, textQuery, "edition-metadata-query/1.4.0", providerVersion);
        EditionMetadataProposal? loaded = await cache.TryReadAsync(textQuery, RetrievedAt.AddDays(-1), CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(EditionMetadataProposalStatus.NotFound);
        loaded.ProblemCode.Should().Be(problemCode);
        loaded.RetrievedAtUtc.Should().Be(RetrievedAt);

        using TemporaryDirectory identifierDirectory = new();
        FileEditionMetadataProposalCache identifierCache = new(new(identifierDirectory.Path));
        EditionMetadataLookupQuery identifierQuery = new(new(1), provider,
            EditionMetadataQueryFields.Identifier, "ISBN:9780141439518", null, null, null);
        await identifierCache.WriteAsync(identifierQuery, new(identifierQuery.BookId, identifierQuery.Provider,
            identifierQuery.Fields, RetrievedAt, EditionMetadataProposalStatus.NotFound), CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(identifierDirectory.Path, identifierQuery,
            "edition-metadata-query/1.4.0", providerVersion);
        (await identifierCache.TryReadAsync(identifierQuery, RetrievedAt.AddDays(-1), CancellationToken.None))!
            .Status.Should().Be(EditionMetadataProposalStatus.NotFound);
    }

    [Theory]
    [InlineData("edition-metadata-query/1.5.0", "open-library-edition-search/1.2.0")]
    [InlineData("edition-metadata-query/1.4.0", "open-library-edition-search/1.1.0")]
    [InlineData("edition-metadata-query/1.3.0", "open-library-edition-search/1.1.0")]
    public async Task UncertainPolicyAndOlderExtractionMissesStillRefresh(string policy, string providerVersion)
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = Query(provider: CurrentProvider);
        await cache.WriteAsync(query, new(query.BookId, query.Provider, query.Fields, RetrievedAt,
            EditionMetadataProposalStatus.NotFound, problemCode: "OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS"), CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query, policy, providerVersion);

        (await cache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task NormalizedPseudonymQueryDoesNotReuseMissForOriginalAnnotatedQueryInputs()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        const string annotatedName = "Morgan Vale (pseud. van Alex Writer)";
        EditionMetadataLookupQuery oldQuery = new(new(1), CurrentProvider,
            EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors,
            null, "The Book", [annotatedName], null);
        await cache.WriteAsync(oldQuery, new(oldQuery.BookId, oldQuery.Provider, oldQuery.Fields,
            RetrievedAt, EditionMetadataProposalStatus.NotFound), CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, oldQuery,
            "edition-metadata-query/1.4.0", CurrentProvider.Version);
        CalibreBook book = new(new(1), "The Book", "Vale, Morgan",
            [new(new(1), annotatedName, "Vale, Morgan")], [], [], "Author/Book (1)");
        EditionMetadataLookupQuery normalized = EditionMetadataLookupQuery.CreateValidation(book, CurrentProvider);

        normalized.Authors.Should().Equal("Morgan Vale");
        (await cache.TryReadAsync(normalized, RetrievedAt.AddDays(-1), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task CompatiblePreviousMissesAvoidProviderRequestsOnFirstAndWarmPreparation()
    {
        CalibreBook[] books = Enumerable.Range(1, 20).Select(index => new CalibreBook(
            new(index), $"Synthetic Book {index}", "Vale, Morgan",
            [new(new(index), "Morgan Vale", "Vale, Morgan")], [], [], $"Author/Book ({index})")).ToArray();
        IEditionMetadataProvider provider = A.Fake<IEditionMetadataProvider>();
        A.CallTo(() => provider.Identity).Returns(CurrentProvider);
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .Returns(new EditionMetadataSearchResult(CurrentProvider, EditionMetadataSearchStatus.NotFound, RetrievedAt));
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(RetrievedAt.AddHours(1));
        using TemporaryDirectory coldDirectory = new();
        ResolveEditionMetadataProposalsUseCase coldResolver = new([provider], new FileEditionMetadataProposalCache(new(coldDirectory.Path)), clock, new());

        EditionMetadataProvidersBatchResult cold = await coldResolver.ExecuteAsync(books, null, CancellationToken.None);

        cold.Providers[0].ProviderRequests.Should().Be(20);
        using TemporaryDirectory reusedDirectory = new();
        FileEditionMetadataProposalCache reusedCache = new(new(reusedDirectory.Path));
        foreach (CalibreBook book in books)
        {
            EditionMetadataLookupQuery query = EditionMetadataLookupQuery.CreateValidation(book, CurrentProvider);
            await reusedCache.WriteAsync(query, new(query.BookId, query.Provider, query.Fields, RetrievedAt,
                EditionMetadataProposalStatus.NotFound, problemCode: "OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS"), CancellationToken.None);
            await MoveCurrentEntryToLegacyIdentityAsync(reusedDirectory.Path, query,
                "edition-metadata-query/1.4.0", CurrentProvider.Version);
        }
        Fake.ClearRecordedCalls(provider);
        ResolveEditionMetadataProposalsUseCase reusedResolver = new([provider], reusedCache, clock, new());

        EditionMetadataProvidersBatchResult first = await reusedResolver.ExecuteAsync(books, null, CancellationToken.None);
        EditionMetadataProvidersBatchResult warm = await reusedResolver.ExecuteAsync(books, null, CancellationToken.None);

        first.Providers[0].ProviderRequests.Should().Be(0);
        first.Providers[0].CacheHits.Should().Be(20);
        warm.Providers[0].ProviderRequests.Should().Be(0);
        warm.Providers[0].CacheHits.Should().Be(20);
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task PreviousProposalBackfillsCurrentIdentifierAliasesWithoutRenewingItsAge()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = Query(provider: CurrentProvider);
        await cache.WriteAsync(query, Proposal(query), CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query, "edition-metadata-query/1.4.0", CurrentProvider.Version);

        await cache.PrepareIdentifierAliasesAsync(CurrentProvider, RetrievedAt.AddDays(-1), CancellationToken.None);
        EditionMetadataLookupQuery identifier = new(new(99), CurrentProvider,
            EditionMetadataQueryFields.Identifier, "ISBN:9780141439518", null, null, null);
        EditionMetadataProposal? loaded = await cache.TryReadAsync(identifier, RetrievedAt.AddDays(-1), CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.ReasonCodes.Should().Contain("METADATA.EDITION.ISBN_EXACT");
        loaded.RetrievedAtUtc.Should().Be(RetrievedAt);
        (await cache.TryReadAsync(identifier, RetrievedAt.AddSeconds(1), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task PreviousProposalAliasBackfillDoesNotOverwriteNewerCachedIdentifierEvidence()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery textQuery = Query(provider: CurrentProvider);
        await cache.WriteAsync(textQuery, Proposal(textQuery), CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, textQuery,
            "edition-metadata-query/1.4.0", CurrentProvider.Version);
        EditionMetadataLookupQuery identifier = new(new(99), CurrentProvider,
            EditionMetadataQueryFields.Identifier, "ISBN:9780141439518", null, null, null);
        EditionMetadataProposal newer = new(identifier.BookId, identifier.Provider, identifier.Fields,
            RetrievedAt.AddHours(1), EditionMetadataProposalStatus.Proposed,
            Proposal(identifier).Candidate, 9_999, ["METADATA.EDITION.ISBN_EXACT"]);
        await cache.WriteAsync(identifier, newer, CancellationToken.None);

        await cache.PrepareIdentifierAliasesAsync(CurrentProvider, RetrievedAt.AddDays(-1), CancellationToken.None);

        (await cache.TryReadAsync(identifier, RetrievedAt.AddDays(-1), CancellationToken.None))
            .Should().BeEquivalentTo(newer);
    }

    private static EditionMetadataProposal CompatibleProposal(EditionMetadataLookupQuery query) => new(
        query.BookId, query.Provider, query.Fields, RetrievedAt, EditionMetadataProposalStatus.Proposed,
        new("work", "edition", query.Title!, query.Authors, languages: ["en"]), 4_208,
        ["METADATA.EDITION.AUTHOR_COMPATIBLE", "METADATA.EDITION.TITLE_EXACT"]);

    private static EditionMetadataLookupQuery Query(
        CalibreBookId? bookId = null,
        EditionMetadataProviderIdentity? provider = null) => new(
        bookId ?? new(1),
        provider ?? Provider,
        EditionMetadataQueryFields.Title | EditionMetadataQueryFields.Authors
            | EditionMetadataQueryFields.Language,
        null,
        "Private Query Title",
        ["Private Query Author"],
        "en");

    private static EditionMetadataProposal Proposal(EditionMetadataLookupQuery query) => new(
        query.BookId,
        query.Provider,
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

    private static async Task MoveCurrentEntryToLegacyIdentityAsync(
        string root,
        EditionMetadataLookupQuery query,
        string queryPolicyVersion = "edition-metadata-query/1.0.0",
        string providerVersion = "open-library-edition-search/1.0.0")
    {
        string currentPath = Path.Combine(root, Identity(query, EditionMetadataLookupQuery.PolicyVersion, query.Provider.Version) + ".json");
        string legacyIdentity = Identity(query, queryPolicyVersion, providerVersion);
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(currentPath))!;
        document["ProviderVersion"] = providerVersion;
        document["QueryPolicyVersion"] = queryPolicyVersion;
        document["QueryIdentity"] = legacyIdentity;
        await File.WriteAllTextAsync(Path.Combine(root, legacyIdentity + ".json"), document.ToJsonString());
        File.Delete(currentPath);
    }

    private static string Identity(
        EditionMetadataLookupQuery query,
        string queryPolicyVersion,
        string providerVersion)
    {
        StringBuilder canonical = new();
        Append("edition-metadata-proposal-cache/1.0");
        Append(queryPolicyVersion);
        Append(EditionMetadataProposal.PolicyVersion);
        Append(query.Provider.Id);
        Append(providerVersion);
        Append(((int)query.Fields).ToString(CultureInfo.InvariantCulture));
        Append(query.Identifier ?? string.Empty);
        Append(query.Title ?? string.Empty);
        foreach (string author in query.Authors) Append(author);
        Append(query.Language ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));

        void Append(string value) => canonical
            .Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(value).Append('|');
    }
}
