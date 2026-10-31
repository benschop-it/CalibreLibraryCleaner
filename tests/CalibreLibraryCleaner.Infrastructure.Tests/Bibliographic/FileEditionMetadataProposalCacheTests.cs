using System.Diagnostics;
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
using Xunit.Abstractions;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Bibliographic;

public sealed class FileEditionMetadataProposalCacheTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PromotingLegacyUnavailableEvidenceDoesNotResetItsRetryWindow()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        CalibreBook book = new(new(1), "Generated TTL Book", "Generated Writer",
            [new(new(1), "Generated Writer", "Generated Writer")], [], [], "Writer/Book (1)");
        EditionMetadataLookupQuery query = EditionMetadataLookupQuery.CreateVariants(book, CurrentProvider)[0];
        await cache.WriteAsync(query, new(book.Id, CurrentProvider, query.Fields, RetrievedAt,
            EditionMetadataProposalStatus.Unavailable, problemCode: "OPEN_LIBRARY.HTTP_429"), CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query,
            "edition-metadata-query/1.6.0", CurrentProvider.Version, "edition-metadata-proposal/1.0.0");
        IEditionMetadataProvider provider = A.Fake<IEditionMetadataProvider>();
        A.CallTo(() => provider.Identity).Returns(CurrentProvider);
        DateTimeOffset now = RetrievedAt.AddHours(7);
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .Returns(new EditionMetadataSearchResult(CurrentProvider, EditionMetadataSearchStatus.NotFound, now));
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(now);
        ResolveEditionMetadataProposalsUseCase useCase = new([provider], cache, clock, new());

        EditionMetadataProposalBatchResult result = (await useCase.ExecuteAsync(
            [book], null, CancellationToken.None)).Providers.Single();

        result.ProviderRequests.Should().Be(1);
        result.Proposals[book.Id].Status.Should().Be(EditionMetadataProposalStatus.NotFound);
        result.Proposals[book.Id].RetrievedAtUtc.Should().Be(now);
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task LegacyFileCacheReuseAndWarmPreparationNeedZeroProviderRequests()
    {
        const int records = 1_000;
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        CalibreBook[] books = Enumerable.Range(1, records).Select(id => new CalibreBook(
            new(id), $"Generated Title {id}", "Generated Writer",
            [new(new(id), "Generated Writer", "Generated Writer")], [], [], $"Writer/Book ({id})")).ToArray();
        foreach (CalibreBook book in books)
        {
            EditionMetadataLookupQuery query = EditionMetadataLookupQuery.CreateVariants(book, CurrentProvider)[0];
            EditionMetadataProposalStatus status = (book.Id.Value % 4) switch
            {
                0 => EditionMetadataProposalStatus.Proposed,
                1 => EditionMetadataProposalStatus.Unavailable,
                _ => EditionMetadataProposalStatus.NotFound,
            };
            EditionMetadataCandidate? candidate = status == EditionMetadataProposalStatus.Proposed
                ? new("work", $"edition-{book.Id.Value}", book.Title, ["Generated Writer"]) : null;
            EditionMetadataProposal proposal = new(book.Id, CurrentProvider, query.Fields, RetrievedAt, status,
                candidate, candidate is null ? 0 : 4_001,
                candidate is null ? null : ["METADATA.EDITION.TITLE_EXACT", "METADATA.EDITION.AUTHOR_EXACT"],
                status == EditionMetadataProposalStatus.Unavailable ? "OPEN_LIBRARY.HTTP_429" : null);
            await cache.WriteAsync(query, proposal, CancellationToken.None);
            await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query,
                "edition-metadata-query/1.6.0", CurrentProvider.Version, "edition-metadata-proposal/1.0.0");
        }
        IEditionMetadataProvider provider = A.Fake<IEditionMetadataProvider>();
        A.CallTo(() => provider.Identity).Returns(CurrentProvider);
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("Generated compatible cache must not invoke a provider."));
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(RetrievedAt.AddHours(1));
        ResolveEditionMetadataProposalsUseCase useCase = new([provider], cache, clock, new());
        Stopwatch timer = Stopwatch.StartNew();

        EditionMetadataProposalBatchResult first = (await useCase.ExecuteAsync(
            books, null, CancellationToken.None)).Providers.Single();
        long firstMilliseconds = timer.ElapsedMilliseconds;
        int promotedCount = Directory.GetFiles(directory.Path, "*.json").Length;
        timer.Restart();
        EditionMetadataProposalBatchResult warm = (await useCase.ExecuteAsync(
            books, null, CancellationToken.None)).Providers.Single();
        long warmMilliseconds = timer.ElapsedMilliseconds;

        first.CompletedQueryCount.Should().Be(records);
        first.CacheHits.Should().Be(records);
        first.ProviderRequests.Should().Be(0);
        warm.CompletedQueryCount.Should().Be(records);
        warm.CacheHits.Should().Be(records);
        warm.ProviderRequests.Should().Be(0);
        warm.Proposals.Values.Should().OnlyContain(value => value.RetrievedAtUtc == RetrievedAt);
        first.Proposals.Values.Concat(warm.Proposals.Values).Should().OnlyContain(value =>
            value.CacheOrigin != null
            && value.CacheOrigin.QueryPolicyVersion == "edition-metadata-query/1.6.0"
            && value.CacheOrigin.ProposalPolicyVersion == "edition-metadata-proposal/1.0.0");
        warm.Proposals.Values.Count(value => value.Status == EditionMetadataProposalStatus.Proposed).Should().Be(250);
        warm.Proposals.Values.Count(value => value.Status == EditionMetadataProposalStatus.Unavailable).Should().Be(250);
        warm.Proposals.Values.Count(value => value.Status == EditionMetadataProposalStatus.NotFound).Should().Be(500);
        promotedCount.Should().Be(records * 2);
        Directory.GetFiles(directory.Path, "*.json").Should().HaveCount(promotedCount);
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .MustNotHaveHappened();
        using Process process = Process.GetCurrentProcess();
        output.WriteLine($"generatedLegacyRecords={records}; legacyReuseMs={firstMilliseconds}; warmMs={warmMilliseconds}; "
            + $"legacyCacheHits={first.CacheHits}; warmCacheHits={warm.CacheHits}; providerRequests=0; "
            + $"promotedEntries={records}; warmAdditionalEntries=0; peakProcessBytes={process.PeakWorkingSet64}; "
            + "adapter=real-file-cache/fake-provider; timestampsPreserved=true");
    }

    [Fact]
    public async Task LegacyCoauthorFactsSurvivePromotionWithoutInventedSourceOrder()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = new(new(1), CurrentProvider, EditionMetadataQueryFields.Identifier,
            "ISBN:9780306406157", null, null, null);
        EditionMetadataCandidate candidate = new("work", "edition", "Title",
            ["Lannert, Stacy", "Kemp, Kristen"], [new("isbn", "9780306406157")], "Publisher");
        EditionMetadataProposal proposal = EditionMetadataProposalPolicy.Resolve(query,
            new(CurrentProvider, EditionMetadataSearchStatus.Success, RetrievedAt, [candidate]));
        await cache.WriteAsync(query, proposal, CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query,
            "edition-metadata-query/1.6.0", CurrentProvider.Version, "edition-metadata-proposal/1.0.0");

        EditionMetadataProposal? first = await cache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None);
        EditionMetadataProposal? warm = await cache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None);

        first.Should().NotBeNull();
        first!.Candidate!.AuthorOrderKnown.Should().BeFalse();
        first.Candidate.Authors.Should().Equal("Kemp, Kristen", "Lannert, Stacy");
        first.Candidate.Publisher.Should().Be("Publisher");
        warm!.Candidate!.AuthorOrderKnown.Should().BeFalse();
        warm.RetrievedAtUtc.Should().Be(RetrievedAt);
        warm.CacheOrigin.Should().BeEquivalentTo(first.CacheOrigin);
        CalibreBook book = new(new(1), "Title", "Source Sort",
            [new(new(1), "Lannert| Stacy| Kemp| Kristen", "Source Sort")], [], [], "Author/Title (1)");
        EditionMetadataAuthorWritePolicy.TryPrepare(book, warm.Candidate, out IReadOnlyList<string> prepared)
            .Should().BeTrue();
        prepared.Should().Equal("Stacy Lannert", "Kristen Kemp");
    }

    [Fact]
    public async Task LegacyPositiveIdentifierAliasesRetainAuthorOrderCoverageAndRetrievalTime()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = Query(provider: CurrentProvider);
        EditionMetadataCandidate candidate = new("work", "edition", "Generated Title",
            ["Lannert, Stacy", "Kemp, Kristen"], [new("isbn", "9780306406157")]);
        await cache.WriteAsync(query, new(query.BookId, query.Provider, query.Fields, RetrievedAt,
            EditionMetadataProposalStatus.Proposed, candidate, 4_001, ["METADATA.EDITION.TITLE_EXACT"]),
            CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query,
            "edition-metadata-query/1.6.0", CurrentProvider.Version, "edition-metadata-proposal/1.0.0");

        await cache.PrepareIdentifierAliasesAsync(CurrentProvider, RetrievedAt.AddDays(-1), CancellationToken.None);
        EditionMetadataLookupQuery isbn = new(new(2), CurrentProvider, EditionMetadataQueryFields.Identifier,
            "ISBN:9780306406157", null, null, null);
        EditionMetadataProposal? alias = await cache.TryReadAsync(isbn, RetrievedAt.AddDays(-1), CancellationToken.None);

        alias.Should().NotBeNull();
        alias!.Candidate!.AuthorOrderKnown.Should().BeFalse();
        alias.RetrievedAtUtc.Should().Be(RetrievedAt);
        alias.ReasonCodes.Should().Contain("METADATA.EDITION.ISBN_EXACT");
        alias.CacheOrigin!.QueryPolicyVersion.Should().Be("edition-metadata-query/1.6.0");
        alias.CacheOrigin.ProposalPolicyVersion.Should().Be("edition-metadata-proposal/1.0.0");
    }

    [Theory]
    [InlineData("edition-metadata-query/1.5.0", "edition-metadata-proposal/1.0.0")]
    [InlineData("edition-metadata-query/1.7.0", "edition-metadata-proposal/0.0.0")]
    public async Task UnsupportedInterpretationsAndKnownOlderExtractionMissesRemainMisses(
        string queryVersion, string proposalVersion)
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = Query(provider: CurrentProvider);
        await cache.WriteAsync(query, new(query.BookId, query.Provider, query.Fields, RetrievedAt,
            EditionMetadataProposalStatus.NotFound, problemCode: "OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS"),
            CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query,
            queryVersion, CurrentProvider.Version, proposalVersion);

        (await cache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None)).Should().BeNull();
        Directory.GetFiles(directory.Path, "*.json").Should().ContainSingle();
    }

    [Theory]
    [InlineData("edition-metadata-query/1.7.0", EditionMetadataProposalStatus.NotFound, null)]
    [InlineData("edition-metadata-query/1.6.0", EditionMetadataProposalStatus.NotFound, null)]
    [InlineData("edition-metadata-query/1.7.0", EditionMetadataProposalStatus.Ambiguous, null)]
    [InlineData("edition-metadata-query/1.6.0", EditionMetadataProposalStatus.Unavailable, "OPEN_LIBRARY.HTTP_429")]
    public async Task CompatibleLegacyNeutralOutcomesReuseExactKeysWithoutInventingFreshness(
        string queryVersion, EditionMetadataProposalStatus status, string? problemCode)
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = Query(provider: CurrentProvider);
        EditionMetadataProposal neutral = new(query.BookId, query.Provider, query.Fields, RetrievedAt,
            status, problemCode: problemCode);
        await cache.WriteAsync(query, neutral, CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query, queryVersion, CurrentProvider.Version,
            "edition-metadata-proposal/1.0.0");

        EditionMetadataProposal? first = await cache.TryReadAsync(
            query, RetrievedAt.AddDays(-1), CancellationToken.None);
        EditionMetadataProposal? warm = await cache.TryReadAsync(
            query, RetrievedAt.AddDays(-1), CancellationToken.None);

        first.Should().NotBeNull();
        first!.Status.Should().Be(status);
        first.ProblemCode.Should().Be(problemCode);
        first.RetrievedAtUtc.Should().Be(RetrievedAt);
        first.CacheOrigin.Should().NotBeNull();
        first.CacheOrigin!.ProposalPolicyVersion.Should().Be("edition-metadata-proposal/1.0.0");
        first.CacheOrigin.QueryPolicyVersion.Should().Be(queryVersion);
        first.CacheOrigin.QueryIdentity.Should().Be(Identity(
            query, queryVersion, CurrentProvider.Version, "edition-metadata-proposal/1.0.0"));
        warm.Should().BeEquivalentTo(first);
        Directory.GetFiles(directory.Path, "*.json").Should().HaveCount(2,
            "a compatible result is promoted without deleting the legacy evidence");
    }

    [Theory]
    [InlineData("edition-metadata-query/1.7.0")]
    [InlineData("edition-metadata-query/1.6.0")]
    public async Task CompatibleLegacySingleCreditIsLocallyRevalidatedInsteadOfFetchedAgain(string queryVersion)
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = Query(provider: CurrentProvider);
        await cache.WriteAsync(query, CompatibleProposal(query), CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query, queryVersion, CurrentProvider.Version,
            "edition-metadata-proposal/1.0.0");

        EditionMetadataProposal? result = await cache.TryReadAsync(
            query, RetrievedAt.AddDays(-1), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Status.Should().Be(EditionMetadataProposalStatus.Proposed);
        result.Candidate!.Authors.Should().Equal(query.Authors);
        result.Candidate.AuthorOrderKnown.Should().BeTrue();
        result.RetrievedAtUtc.Should().Be(RetrievedAt);
        result.CacheOrigin!.Provider.Should().Be(CurrentProvider);
        result.ReasonCodes.Should().Contain("METADATA.EDITION.AUTHOR_EXACT");
    }

    [Fact]
    public async Task CompleteOrderedCreditsRoundTripWithoutAlphabeticalReordering()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = new(
            new(1), CurrentProvider, EditionMetadataQueryFields.Identifier,
            "ISBN:9780306406157", null, null, null);
        EditionMetadataCandidate candidate = new("work", "edition", "Title",
            ["Lannert, Stacy", "Kemp, Kristen"], [new("isbn", "9780306406157")]);
        EditionMetadataProposal proposal = EditionMetadataProposalPolicy.Resolve(query,
            new(CurrentProvider, EditionMetadataSearchStatus.Success, RetrievedAt, [candidate]));

        await cache.WriteAsync(query, proposal, CancellationToken.None);
        string currentPath = Directory.GetFiles(directory.Path, "*.json").Single();
        JsonNode existingCurrentDocument = JsonNode.Parse(await File.ReadAllTextAsync(currentPath))!;
        existingCurrentDocument["Candidate"]!.AsObject().Remove("AuthorOrderKnown");
        await File.WriteAllTextAsync(currentPath, existingCurrentDocument.ToJsonString());
        EditionMetadataProposal? loaded = await cache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Candidate!.Authors.Should().Equal("Lannert, Stacy", "Kemp, Kristen");
        loaded.Candidate.AuthorOrderKnown.Should().BeTrue();
        EditionMetadataAuthorWritePolicy.TryPrepare(loaded.Candidate.Authors, out IReadOnlyList<string> prepared)
            .Should().BeTrue();
        prepared.Should().Equal("Stacy Lannert", "Kristen Kemp");
    }

    [Fact]
    public async Task AChangedProposalHeaderWithoutItsCorrespondingIdentityRemainsAnExactLookupMiss()
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = Query(provider: CurrentProvider);
        await cache.WriteAsync(query, CompatibleProposal(query), CancellationToken.None);
        string path = Directory.GetFiles(directory.Path, "*.json").Single();
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["ProposalPolicyVersion"] = "edition-metadata-proposal/1.0.0";
        await File.WriteAllTextAsync(path, document.ToJsonString());

        await cache.PrepareIdentifierAliasesAsync(CurrentProvider, RetrievedAt.AddDays(-1), CancellationToken.None);
        EditionMetadataProposal? loaded = await cache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None);

        loaded.Should().BeNull("the changed proposal-version header does not match the stored hashed identity");
        File.Exists(path).Should().BeTrue("invalidating evidence must not delete existing cache artifacts");
        Directory.GetFiles(directory.Path, "*.json").Should().ContainSingle();
    }

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
        loaded.CacheOrigin!.Provider.Should().Be(new EditionMetadataProviderIdentity(
            "open-library", "open-library-edition-search/1.0.0"));

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

    [Theory]
    [InlineData(EditionMetadataProposalStatus.Proposed)]
    [InlineData(EditionMetadataProposalStatus.NotFound)]
    public async Task EarlierProviderWithCurrentQueryCannotBypassValidationOrExtractionRefresh(
        EditionMetadataProposalStatus status)
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery query = Query(provider: CurrentProvider);
        EditionMetadataProposal proposal = status == EditionMetadataProposalStatus.Proposed
            ? Proposal(query)
            : new(query.BookId, CurrentProvider, query.Fields, RetrievedAt, status,
                problemCode: "OPEN_LIBRARY.NO_COMPATIBLE_EDITIONS");
        await cache.WriteAsync(query, proposal, CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, query,
            EditionMetadataLookupQuery.PolicyVersion, "open-library-edition-search/1.1.0");

        (await cache.TryReadAsync(query, RetrievedAt.AddDays(-1), CancellationToken.None))
            .Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocallyRevalidatedOlderProviderEvidenceSeedsAliasesAfterTheBackfillMarker(
        bool newerAliasExists)
    {
        using TemporaryDirectory directory = new();
        FileEditionMetadataProposalCache cache = new(new(directory.Path));
        EditionMetadataLookupQuery text = Query(provider: CurrentProvider);
        EditionMetadataCandidate candidate = new("work", "legacy-edition", text.Title!, text.Authors,
            [new("isbn", "9780141439518")], languages: ["en"]);
        EditionMetadataProposal source = EditionMetadataProposalPolicy.Resolve(text,
            new(CurrentProvider, EditionMetadataSearchStatus.Success, RetrievedAt, [candidate]));
        await cache.WriteAsync(text, source, CancellationToken.None);
        await MoveCurrentEntryToLegacyIdentityAsync(directory.Path, text,
            proposalPolicyVersion: "edition-metadata-proposal/1.0.0");
        await cache.PrepareIdentifierAliasesAsync(CurrentProvider, RetrievedAt.AddDays(-1), CancellationToken.None);
        EditionMetadataLookupQuery identifier = new(new(99), CurrentProvider,
            EditionMetadataQueryFields.Identifier, "ISBN:9780141439518", null, null, null);
        EditionMetadataProposal newer = new(identifier.BookId, CurrentProvider, identifier.Fields,
            RetrievedAt.AddHours(1), EditionMetadataProposalStatus.Proposed,
            new("work", "newer-edition", text.Title!, text.Authors, candidate.Identifiers), 9_999,
            ["METADATA.EDITION.ISBN_EXACT"]);
        if (newerAliasExists) await cache.WriteAsync(identifier, newer, CancellationToken.None);

        EditionMetadataProposal? revalidated = await cache.TryReadAsync(
            text, RetrievedAt.AddDays(-1), CancellationToken.None);
        await cache.PrepareIdentifierAliasesAsync(CurrentProvider, RetrievedAt.AddDays(-1), CancellationToken.None);
        EditionMetadataProposal? alias = await cache.TryReadAsync(
            identifier, RetrievedAt.AddDays(-1), CancellationToken.None);

        revalidated.Should().NotBeNull();
        alias.Should().NotBeNull("local promotion must seed aliases even after the one-time pass completed");
        if (newerAliasExists)
            alias.Should().BeEquivalentTo(newer);
        else
        {
            alias!.Candidate!.EditionId.Should().Be("legacy-edition");
            alias.RetrievedAtUtc.Should().Be(RetrievedAt);
            alias.CacheOrigin.Should().BeEquivalentTo(revalidated!.CacheOrigin);
        }
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
        string providerVersion = "open-library-edition-search/1.0.0",
        string? proposalPolicyVersion = null)
    {
        string currentPath = Path.Combine(root, Identity(query, EditionMetadataLookupQuery.PolicyVersion, query.Provider.Version) + ".json");
        string legacyIdentity = Identity(query, queryPolicyVersion, providerVersion, proposalPolicyVersion);
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(currentPath))!;
        document["ProviderVersion"] = providerVersion;
        document["QueryPolicyVersion"] = queryPolicyVersion;
        document["QueryIdentity"] = legacyIdentity;
        document["ProposalPolicyVersion"] = proposalPolicyVersion ?? EditionMetadataProposal.PolicyVersion;
        if (proposalPolicyVersion == "edition-metadata-proposal/1.0.0"
            && document["Candidate"] is JsonObject candidate)
        {
            candidate.Remove("AuthorOrderKnown");
            string[] ordered = candidate["Authors"]!.AsArray()
                .Select(value => value!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
            JsonArray legacyAuthors = new();
            foreach (string author in ordered) legacyAuthors.Add(author);
            candidate["Authors"] = legacyAuthors;
        }
        await File.WriteAllTextAsync(Path.Combine(root, legacyIdentity + ".json"), document.ToJsonString());
        File.Delete(currentPath);
    }

    private static string Identity(
        EditionMetadataLookupQuery query,
        string queryPolicyVersion,
        string providerVersion,
        string? proposalPolicyVersion = null)
    {
        StringBuilder canonical = new();
        Append("edition-metadata-proposal-cache/1.0");
        Append(queryPolicyVersion);
        Append(proposalPolicyVersion ?? EditionMetadataProposal.PolicyVersion);
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
