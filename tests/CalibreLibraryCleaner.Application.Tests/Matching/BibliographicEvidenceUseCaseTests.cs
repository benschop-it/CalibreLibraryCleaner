using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Matching;

public sealed class BibliographicEvidenceUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly BibliographicProviderIdentity ProviderIdentity = new(
        "open-library", "open-library-search-1.0.0");

    [Fact]
    public async Task ColdRunQueriesUnresolvedRecordsAndWarmRunUsesOnlyCache()
    {
        CalibreBook[] books =
        [
            Book(1, "Alice's Adventures in Wonderland", "Lewis Carroll"),
            Book(2, "Alice in Wonderland Illustrated", "L. Carroll"),
        ];
        BookMatchingProfile[] profiles = books.Select(Profile).ToArray();
        BookCandidatePair pair = Pair(1, 2);
        RecordingProvider provider = new(query => Search(
            new BibliographicWorkCandidate(
                "/works/OL138052W", "Alice's Adventures in Wonderland", ["Lewis Carroll"], ["eng"], 400)));
        MemoryCache cache = new();
        ResolveBibliographicEvidenceUseCase useCase = UseCase(provider, cache);

        BibliographicEvidenceBatchResult cold = await useCase.ExecuteAsync(
            books, profiles, [pair], new Dictionary<BookCandidatePairId, CandidateContentComparison>(),
            null, CancellationToken.None);
        BibliographicEvidenceBatchResult warm = await useCase.ExecuteAsync(
            books, profiles, [pair], new Dictionary<BookCandidatePairId, CandidateContentComparison>(),
            null, CancellationToken.None);

        cold.ProviderRequests.Should().Be(2);
        cold.CacheHits.Should().Be(0);
        cold.MatchedRecords.Should().Be(2);
        cold.Pairs.Single().HasAnchor.Should().BeTrue();
        warm.ProviderRequests.Should().Be(0);
        warm.CacheHits.Should().Be(2);
        provider.RequestCount.Should().Be(2);
        cache.WriteCount.Should().Be(2);
    }

    [Fact]
    public async Task LocallyConfirmedAndDecisivelyContradictedPairsAreNotQueried()
    {
        CalibreBook[] books =
        [
            Book(1, "First Work", "Alice Example"),
            Book(2, "First Work Revised", "Alice Example"),
            Book(3, "Second Work", "Bob Example"),
            Book(4, "Second Work Revised", "Bob Example"),
        ];
        BookCandidatePair anchor = new(
            new(new(1), new(2)), 2000,
            [new("MATCH.BINARY.EXACT", CandidateEvidenceStrength.Anchor)], [], false);
        BookCandidatePair rejected = new(
            new(new(3), new(4)), 900,
            [new("MATCH.TITLE.TOKEN_MEDIUM", CandidateEvidenceStrength.Supporting)],
            [new("MATCH.LANGUAGE.CONFLICT")], true);
        RecordingProvider provider = new(_ => throw new InvalidOperationException("Provider should not run."));

        BibliographicEvidenceBatchResult result = await UseCase(provider, new MemoryCache()).ExecuteAsync(
            books, books.Select(Profile).ToArray(), [anchor, rejected],
            new Dictionary<BookCandidatePairId, CandidateContentComparison>(), null, CancellationToken.None);

        result.QueryCount.Should().Be(0);
        result.ProviderRequests.Should().Be(0);
        provider.RequestCount.Should().Be(0);
        result.Pairs.Should().Equal(anchor, rejected);
    }

    [Fact]
    public async Task ProviderUnavailableLeavesLocalPairUnchangedAndReportsFailure()
    {
        CalibreBook[] books =
        [
            Book(1, "Clockwork Harbor", "Clara Maker"),
            Book(2, "Clockwork Harbor Annotated", "C. Maker"),
        ];
        BookCandidatePair pair = Pair(1, 2);
        RecordingProvider provider = new(_ => new(
            ProviderIdentity,
            BibliographicSearchStatus.Unavailable,
            Now,
            problemCode: "OPEN_LIBRARY.TIMEOUT"));

        BibliographicEvidenceBatchResult result = await UseCase(provider, new MemoryCache()).ExecuteAsync(
            books, books.Select(Profile).ToArray(), [pair],
            new Dictionary<BookCandidatePairId, CandidateContentComparison>(), null, CancellationToken.None);

        result.Failures.Should().Be(2);
        result.Pairs.Single().HasAnchor.Should().BeFalse();
        result.Resolutions.Values.Should().OnlyContain(value =>
            value.Status == BibliographicResolutionStatus.Unavailable);
    }

    [Fact]
    public async Task ProviderRequestLimitFailsOpenForRemainingRecords()
    {
        CalibreBook[] books =
        [
            Book(1, "First Shared Work", "Alice Example"),
            Book(2, "First Shared Work Revised", "Alice Example"),
            Book(3, "Second Shared Work", "Bob Example"),
            Book(4, "Second Shared Work Revised", "Bob Example"),
        ];
        RecordingProvider provider = new(_ => Search(
            new BibliographicWorkCandidate(
                "/works/OL1W", "First Shared Work", ["Alice Example"], ["eng"], 20)));
        ResolveBibliographicEvidenceUseCase useCase = UseCase(
            provider, new MemoryCache(), new(maximumProviderRequestsPerRun: 1));

        BibliographicEvidenceBatchResult result = await useCase.ExecuteAsync(
            books, books.Select(Profile).ToArray(), [Pair(1, 2), Pair(3, 4)],
            new Dictionary<BookCandidatePairId, CandidateContentComparison>(), null, CancellationToken.None);

        result.ProviderRequests.Should().Be(1);
        result.RequestLimitReached.Should().BeTrue();
        provider.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task ThrownProviderAndCacheFailuresStillReturnLocalPairs()
    {
        CalibreBook[] books =
        [
            Book(1, "Clockwork Harbor", "Clara Maker"),
            Book(2, "Clockwork Harbor Annotated", "C. Maker"),
        ];
        IBibliographicProvider provider = A.Fake<IBibliographicProvider>();
        A.CallTo(() => provider.Identity).Returns(ProviderIdentity);
        A.CallTo(() => provider.SearchAsync(A<BibliographicLookupQuery>._, A<CancellationToken>._))
            .ThrowsAsync(new HttpRequestException("Private payload must not escape."));
        IBibliographicResolutionCache cache = A.Fake<IBibliographicResolutionCache>();
        A.CallTo(() => cache.TryReadAsync(
                A<BibliographicLookupQuery>._, A<DateTimeOffset>._, A<CancellationToken>._))
            .ThrowsAsync(new IOException("Cache unavailable."));
        A.CallTo(() => cache.WriteAsync(
                A<BibliographicLookupQuery>._,
                A<BibliographicWorkResolution>._,
                A<CancellationToken>._))
            .ThrowsAsync(new IOException("Cache unavailable."));
        A.CallTo(() => cache.PruneAsync(A<CancellationToken>._))
            .ThrowsAsync(new IOException("Cache unavailable."));

        BibliographicEvidenceBatchResult result = await UseCase(provider, cache).ExecuteAsync(
            books, books.Select(Profile).ToArray(), [Pair(1, 2)],
            new Dictionary<BookCandidatePairId, CandidateContentComparison>(), null, CancellationToken.None);

        result.Failures.Should().Be(2);
        result.Pairs.Single().HasAnchor.Should().BeFalse();
        result.Resolutions.Values.Should().OnlyContain(value =>
            value.Status == BibliographicResolutionStatus.Unavailable
            && value.ProblemCode == "BIBLIOGRAPHIC_PROVIDER.UNAVAILABLE");
    }

    private static ResolveBibliographicEvidenceUseCase UseCase(
        IBibliographicProvider provider,
        IBibliographicResolutionCache cache,
        BibliographicEnrichmentOptions? options = null)
    {
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        return new(provider, cache, clock, options ?? new());
    }

    private static BibliographicSearchResult Search(params BibliographicWorkCandidate[] candidates) => new(
        ProviderIdentity, BibliographicSearchStatus.Success, Now, candidates);

    private static CalibreBook Book(long id, string title, string author) => new(
        new(id),
        title,
        author,
        [new(null, author, author)],
        [],
        [],
        $"Book {id}",
        new(languages: ["eng"]));

    private static BookMatchingProfile Profile(CalibreBook book) =>
        BookMatchingProfileFactory.Create([book]).Single();

    private static BookCandidatePair Pair(long first, long second) => new(
        new(new(first), new(second)),
        900,
        [new("MATCH.TITLE.TOKEN_MEDIUM", CandidateEvidenceStrength.Supporting)],
        [],
        true);

    private sealed class RecordingProvider(Func<BibliographicLookupQuery, BibliographicSearchResult> search) :
        IBibliographicProvider
    {
        public BibliographicProviderIdentity Identity => ProviderIdentity;
        public int RequestCount { get; private set; }

        public Task<BibliographicSearchResult> SearchAsync(
            BibliographicLookupQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(search(query));
        }
    }

    private sealed class MemoryCache : IBibliographicResolutionCache
    {
        private readonly Dictionary<string, BibliographicWorkResolution> _values = new(StringComparer.Ordinal);

        public int WriteCount { get; private set; }

        public Task<BibliographicWorkResolution?> TryReadAsync(
            BibliographicLookupQuery query,
            DateTimeOffset minimumRetrievedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryGetValue(query.QueryIdentity, out BibliographicWorkResolution? value);
            return Task.FromResult(value is not null && value.RetrievedAtUtc >= minimumRetrievedAtUtc
                ? value
                : null);
        }

        public Task WriteAsync(
            BibliographicLookupQuery query,
            BibliographicWorkResolution resolution,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[query.QueryIdentity] = resolution;
            WriteCount++;
            return Task.CompletedTask;
        }

        public Task PruneAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
