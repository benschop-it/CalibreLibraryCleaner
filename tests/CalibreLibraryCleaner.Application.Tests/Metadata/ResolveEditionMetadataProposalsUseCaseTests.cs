using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Metadata;

public sealed class ResolveEditionMetadataProposalsUseCaseTests
{
    private static readonly EditionMetadataProviderIdentity ProviderIdentity = new(
        "open-library", "open-library-edition-search/1.0.0");
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DuplicateQueriesUseOneProviderRequestAndReturnEveryBookProposal()
    {
        IEditionMetadataProvider provider = A.Fake<IEditionMetadataProvider>();
        A.CallTo(() => provider.Identity).Returns(ProviderIdentity);
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult(Search(
                call.GetArgument<EditionMetadataLookupQuery>(0)!)));
        IEditionMetadataProposalCache cache = A.Fake<IEditionMetadataProposalCache>();
        ResolveEditionMetadataProposalsUseCase useCase = Create(provider, cache);

        EditionMetadataProposalBatchResult result = (await useCase.ExecuteAsync(
            [Book(1), Book(2)], null, CancellationToken.None)).Providers.Single();

        result.QueryCount.Should().Be(1);
        result.ProviderRequests.Should().Be(1);
        result.Proposals.Keys.Should().BeEquivalentTo([new CalibreBookId(1), new CalibreBookId(2)]);
        result.Proposals.Values.Should().OnlyContain(value =>
            value.Status == EditionMetadataProposalStatus.Proposed
            && value.Candidate!.EditionId == "/books/OL7353617M");
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CacheHitAvoidsProviderAndRebindsProposalToCurrentBook()
    {
        IEditionMetadataProvider provider = A.Fake<IEditionMetadataProvider>();
        A.CallTo(() => provider.Identity).Returns(ProviderIdentity);
        IEditionMetadataProposalCache cache = A.Fake<IEditionMetadataProposalCache>();
        A.CallTo(() => cache.TryReadAsync(
                A<EditionMetadataLookupQuery>._,
                A<DateTimeOffset>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<EditionMetadataProposal?>(Proposal(
                call.GetArgument<EditionMetadataLookupQuery>(0)!)));
        ResolveEditionMetadataProposalsUseCase useCase = Create(provider, cache);

        EditionMetadataProposalBatchResult result = (await useCase.ExecuteAsync(
            [Book(7)], null, CancellationToken.None)).Providers.Single();

        result.CacheHits.Should().Be(1);
        result.ProviderRequests.Should().Be(0);
        result.Proposals[new(7)].BookId.Should().Be(new CalibreBookId(7));
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task RequestLimitStopsAtTrueProcessedCountInsteadOfJumpingToTotal()
    {
        IEditionMetadataProvider provider = A.Fake<IEditionMetadataProvider>();
        A.CallTo(() => provider.Identity).Returns(ProviderIdentity);
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult(Search(
                call.GetArgument<EditionMetadataLookupQuery>(0)!)));
        IEditionMetadataProposalCache cache = A.Fake<IEditionMetadataProposalCache>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        List<EditionMetadataEnrichmentProgress> progress = [];
        ResolveEditionMetadataProposalsUseCase useCase = new(
            [provider], cache, clock, new(maximumProviderRequestsPerRun: 1));

        EditionMetadataProposalBatchResult result = (await useCase.ExecuteAsync(
            [Book(1, "9780141439518"), Book(2, "9780306406157"), Book(3, "9780140328721")],
            new InlineProgress(progress.Add), CancellationToken.None)).Providers.Single();

        result.RequestLimitReached.Should().BeTrue();
        result.ProviderRequests.Should().Be(1);
        progress[^1].CompletedQueries.Should().Be(1);
        progress[^1].TotalQueries.Should().Be(3);
        progress[^1].Detail.Should().Contain("2 uncached queries deferred");
        A.CallTo(() => provider.SearchAsync(
            A<EditionMetadataLookupQuery>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task FreshUnavailableResultIsCachedForCooldownAndAvoidsImmediateRetry()
    {
        IEditionMetadataProvider provider = A.Fake<IEditionMetadataProvider>();
        A.CallTo(() => provider.Identity).Returns(ProviderIdentity);
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .ThrowsAsync(new HttpRequestException("private payload"));
        EditionMetadataProposal? stored = null;
        IEditionMetadataProposalCache cache = A.Fake<IEditionMetadataProposalCache>();
        A.CallTo(() => cache.TryReadAsync(
                A<EditionMetadataLookupQuery>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .ReturnsLazily(() => Task.FromResult(stored));
        A.CallTo(() => cache.WriteAsync(
                A<EditionMetadataLookupQuery>._,
                A<EditionMetadataProposal>._,
                A<CancellationToken>._))
            .Invokes(call => stored = call.GetArgument<EditionMetadataProposal>(1));
        ResolveEditionMetadataProposalsUseCase useCase = Create(provider, cache);

        EditionMetadataProposalBatchResult cold = (await useCase.ExecuteAsync(
            [Book(1)], null, CancellationToken.None)).Providers.Single();
        EditionMetadataProposalBatchResult warm = (await useCase.ExecuteAsync(
            [Book(1)], null, CancellationToken.None)).Providers.Single();

        cold.ProviderRequests.Should().Be(1);
        cold.Failures.Should().Be(1);
        warm.ProviderRequests.Should().Be(0);
        warm.CacheHits.Should().Be(1);
        warm.Proposals[new(1)].Status.Should().Be(EditionMetadataProposalStatus.Unavailable);
        A.CallTo(() => provider.SearchAsync(
            A<EditionMetadataLookupQuery>._, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProviderFailureIsUnavailableAndWritesCooldownCacheEntry()
    {
        IEditionMetadataProvider provider = A.Fake<IEditionMetadataProvider>();
        A.CallTo(() => provider.Identity).Returns(ProviderIdentity);
        A.CallTo(() => provider.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .ThrowsAsync(new HttpRequestException("private payload"));
        IEditionMetadataProposalCache cache = A.Fake<IEditionMetadataProposalCache>();
        ResolveEditionMetadataProposalsUseCase useCase = Create(provider, cache);

        EditionMetadataProposalBatchResult result = (await useCase.ExecuteAsync(
            [Book(1)], null, CancellationToken.None)).Providers.Single();

        result.Failures.Should().Be(1);
        result.Proposals[new(1)].Status.Should().Be(EditionMetadataProposalStatus.Unavailable);
        result.Proposals[new(1)].ProblemCode.Should().Be("EDITION_METADATA_PROVIDER.UNAVAILABLE");
        A.CallTo(() => cache.WriteAsync(
                A<EditionMetadataLookupQuery>._,
                A<EditionMetadataProposal>.That.Matches(value =>
                    value.Status == EditionMetadataProposalStatus.Unavailable),
                A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProvidersRemainIndependentAndProgressTextIsProviderNeutral()
    {
        IEditionMetadataProvider available = A.Fake<IEditionMetadataProvider>();
        A.CallTo(() => available.Identity).Returns(ProviderIdentity);
        A.CallTo(() => available.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult(Search(
                call.GetArgument<EditionMetadataLookupQuery>(0)!)));
        EditionMetadataProviderIdentity failingIdentity = new(
            "second-provider", "second-provider/1.0.0");
        IEditionMetadataProvider failing = A.Fake<IEditionMetadataProvider>();
        A.CallTo(() => failing.Identity).Returns(failingIdentity);
        A.CallTo(() => failing.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .ThrowsAsync(new HttpRequestException("private payload"));
        IEditionMetadataProposalCache cache = A.Fake<IEditionMetadataProposalCache>();
        List<EditionMetadataEnrichmentProgress> progress = [];
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        ResolveEditionMetadataProposalsUseCase useCase = new(
            [available, failing], cache, clock, new());

        EditionMetadataProvidersBatchResult result = await useCase.ExecuteAsync(
            [Book(1)], new InlineProgress(progress.Add), CancellationToken.None);

        result.Providers.Should().ContainSingle(value =>
            value.Provider == ProviderIdentity && value.ProposedRecords == 1);
        result.Providers.Should().ContainSingle(value =>
            value.Provider == failingIdentity && value.Failures == 1);
        progress.Select(value => value.Provider).Distinct().Should()
            .BeEquivalentTo([ProviderIdentity, failingIdentity]);
        progress.Should().OnlyContain(value =>
            !value.Detail.Contains("Open Library", StringComparison.Ordinal)
            && !value.Detail.Contains("Google", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnavailableKeyedProviderIsSkippedOnceWithoutBlockingAvailableProvider()
    {
        IEditionMetadataProvider available = A.Fake<IEditionMetadataProvider>();
        A.CallTo(() => available.Identity).Returns(ProviderIdentity);
        A.CallTo(() => available.SearchAsync(A<EditionMetadataLookupQuery>._, A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult(Search(
                call.GetArgument<EditionMetadataLookupQuery>(0)!)));
        UnavailableProvider unavailable = new();
        IEditionMetadataProposalCache cache = A.Fake<IEditionMetadataProposalCache>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        ResolveEditionMetadataProposalsUseCase useCase = new(
            [available, unavailable], cache, clock, new());

        EditionMetadataProvidersBatchResult result = await useCase.ExecuteAsync(
            [Book(1), Book(2)], null, CancellationToken.None);

        result.Providers.Should().ContainSingle(value =>
            value.Provider == ProviderIdentity && value.ProposedRecords == 2);
        result.Providers.Should().ContainSingle(value =>
            value.Provider == unavailable.Identity && !value.Enabled && value.ProviderRequests == 0);
        unavailable.AvailabilityChecks.Should().Be(1);
        unavailable.Searches.Should().Be(0);
    }

    private static ResolveEditionMetadataProposalsUseCase Create(
        IEditionMetadataProvider provider,
        IEditionMetadataProposalCache cache)
    {
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        return new([provider], cache, clock, new());
    }

    private static CalibreBook Book(long id, string isbn = "9780141439518") => new(
        new(id),
        "Pride and Prejudice",
        "Austen, Jane",
        [new(new(id), "Jane Austen", "Austen, Jane")],
        [new("isbn", isbn)],
        [],
        $"Author/Book ({id})");

    private static EditionMetadataSearchResult Search(EditionMetadataLookupQuery query) => new(
        ProviderIdentity,
        EditionMetadataSearchStatus.Success,
        Now,
        [Candidate()]);

    private static EditionMetadataProposal Proposal(EditionMetadataLookupQuery query) => new(
        query.BookId,
        ProviderIdentity,
        query.Fields,
        Now,
        EditionMetadataProposalStatus.Proposed,
        Candidate(),
        10_008,
        ["METADATA.EDITION.ISBN_EXACT"]);

    private static EditionMetadataCandidate Candidate() => new(
        "/works/OL66554W",
        "/books/OL7353617M",
        "Pride and Prejudice",
        ["Jane Austen"],
        [new("isbn", "9780141439518")],
        "Penguin Classics",
        new(2003),
        ["eng"]);

    private sealed class InlineProgress(Action<EditionMetadataEnrichmentProgress> report) :
        IProgress<EditionMetadataEnrichmentProgress>
    {
        public void Report(EditionMetadataEnrichmentProgress value) => report(value);
    }

    private sealed class UnavailableProvider :
        IEditionMetadataProvider,
        IEditionMetadataProviderAvailability
    {
        public EditionMetadataProviderIdentity Identity { get; } = new(
            "keyed-provider", "keyed-provider/1.0.0");
        public int AvailabilityChecks { get; private set; }
        public int Searches { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AvailabilityChecks++;
            return Task.FromResult(false);
        }

        public Task<EditionMetadataSearchResult> SearchAsync(
            EditionMetadataLookupQuery query,
            CancellationToken cancellationToken)
        {
            Searches++;
            throw new InvalidOperationException("Unavailable provider must not be searched.");
        }
    }
}
