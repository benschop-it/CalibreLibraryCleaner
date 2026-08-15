using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Application.Matching;

public interface IBibliographicProvider
{
    BibliographicProviderIdentity Identity { get; }

    Task<BibliographicSearchResult> SearchAsync(
        BibliographicLookupQuery query,
        CancellationToken cancellationToken);
}

public interface IBibliographicResolutionCache
{
    Task<BibliographicWorkResolution?> TryReadAsync(
        BibliographicLookupQuery query,
        DateTimeOffset minimumRetrievedAtUtc,
        CancellationToken cancellationToken);

    Task WriteAsync(
        BibliographicLookupQuery query,
        BibliographicWorkResolution resolution,
        CancellationToken cancellationToken);

    Task PruneAsync(CancellationToken cancellationToken);
}

public sealed record BibliographicEnrichmentOptions
{
    public BibliographicEnrichmentOptions(
        bool enabled = true,
        int maximumProviderRequestsPerRun = 24,
        TimeSpan? maximumCacheAge = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumProviderRequestsPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumProviderRequestsPerRun, 100);
        TimeSpan age = maximumCacheAge ?? TimeSpan.FromDays(30);
        if (age < TimeSpan.FromHours(1) || age > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(maximumCacheAge));
        Enabled = enabled;
        MaximumProviderRequestsPerRun = maximumProviderRequestsPerRun;
        MaximumCacheAge = age;
    }

    public bool Enabled { get; }
    public int MaximumProviderRequestsPerRun { get; }
    public TimeSpan MaximumCacheAge { get; }
}

public sealed record BibliographicEvidenceProgress(
    int CompletedQueries,
    int TotalQueries,
    int CacheHits,
    int ProviderRequests,
    int MatchedRecords,
    int Failures,
    string Detail);

public sealed record BibliographicEvidenceBatchResult(
    IReadOnlyList<BookCandidatePair> Pairs,
    IReadOnlyDictionary<CalibreBookId, BibliographicWorkResolution> Resolutions,
    int QueryCount,
    int CacheHits,
    int ProviderRequests,
    int MatchedRecords,
    int Failures,
    bool RequestLimitReached,
    bool Enabled)
{
    public static BibliographicEvidenceBatchResult Disabled(IReadOnlyList<BookCandidatePair> pairs) =>
        new(pairs, new Dictionary<CalibreBookId, BibliographicWorkResolution>(), 0, 0, 0, 0, 0, false, false);
}

public sealed class ResolveBibliographicEvidenceUseCase(
    IBibliographicProvider provider,
    IBibliographicResolutionCache cache,
    IClock clock,
    BibliographicEnrichmentOptions options)
{
    private static readonly HashSet<string> DecisiveContradictions = new(StringComparer.Ordinal)
    {
        "MATCH.LANGUAGE.CONFLICT",
        "MATCH.SERIES_INDEX.CONFLICT",
        "MATCH.CONTENT.DIFFERENT",
    };

    public async Task<BibliographicEvidenceBatchResult> ExecuteAsync(
        IReadOnlyList<CalibreBook> books,
        IReadOnlyList<BookMatchingProfile> profiles,
        IReadOnlyList<BookCandidatePair> pairs,
        IReadOnlyDictionary<BookCandidatePairId, CandidateContentComparison> contentComparisons,
        IProgress<BibliographicEvidenceProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(books);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(contentComparisons);
        if (!options.Enabled || pairs.Count == 0)
            return BibliographicEvidenceBatchResult.Disabled(pairs);

        HashSet<CalibreBookId> targetIds = pairs.Where(pair => ShouldResolve(pair, contentComparisons))
            .SelectMany(pair => new[] { pair.Id.First, pair.Id.Second }).ToHashSet();
        Dictionary<CalibreBookId, CalibreBook> booksById = books.ToDictionary(value => value.Id);
        Dictionary<CalibreBookId, BookMatchingProfile> profilesById = profiles.ToDictionary(value => value.BookId);
        BibliographicLookupQuery[] queries = targetIds.OrderBy(value => value.Value)
            .Where(value => booksById.ContainsKey(value) && profilesById.ContainsKey(value))
            .Select(value => BibliographicLookupQuery.Create(
                booksById[value], profilesById[value], provider.Identity)).ToArray();
        IGrouping<string, BibliographicLookupQuery>[] groups = queries
            .GroupBy(value => value.QueryIdentity, StringComparer.Ordinal)
            .OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
        Dictionary<CalibreBookId, BibliographicWorkResolution> resolutions = [];
        int completed = 0;
        int cacheHits = 0;
        int requests = 0;
        int failures = 0;
        bool limitReached = false;
        DateTimeOffset minimumRetrievedAt = clock.GetUtcNow().ToUniversalTime() - options.MaximumCacheAge;
        progress?.Report(new(0, groups.Length, 0, 0, 0, 0,
            "Open Library enabled; transmitting identifier or title, author, and language fields for unresolved records."));

        foreach (IGrouping<string, BibliographicLookupQuery> group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BibliographicLookupQuery representative = group.First();
            BibliographicWorkResolution? resolution = await TryReadCacheAsync(
                representative, minimumRetrievedAt, cancellationToken).ConfigureAwait(false);
            if (resolution is not null)
            {
                cacheHits++;
            }
            else if (requests >= options.MaximumProviderRequestsPerRun)
            {
                limitReached = true;
            }
            else
            {
                requests++;
                BibliographicSearchResult search = await SearchAsync(
                    representative, cancellationToken).ConfigureAwait(false);
                resolution = BibliographicResolutionPolicy.Resolve(representative, search);
                if (resolution.Status == BibliographicResolutionStatus.Unavailable) failures++;
                if (resolution.Status != BibliographicResolutionStatus.Unavailable)
                    await TryWriteCacheAsync(representative, resolution, cancellationToken).ConfigureAwait(false);
            }

            if (resolution is not null)
            {
                foreach (BibliographicLookupQuery query in group)
                    resolutions[query.BookId] = ForBook(resolution, query.BookId);
            }
            completed++;
            progress?.Report(new(
                completed,
                groups.Length,
                cacheHits,
                requests,
                resolutions.Values.Count(value => value.Status == BibliographicResolutionStatus.Matched),
                failures,
                limitReached ? "Open Library request limit reached; remaining records use local evidence."
                    : "Resolving bounded Open Library work identities."));
        }

        await TryPruneCacheAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<BookCandidatePair> enriched = BibliographicPairEvidencePolicy.Enrich(pairs, resolutions);
        return new(
            enriched,
            resolutions,
            groups.Length,
            cacheHits,
            requests,
            resolutions.Values.Count(value => value.Status == BibliographicResolutionStatus.Matched),
            failures,
            limitReached,
            true);
    }

    private static bool ShouldResolve(
        BookCandidatePair pair,
        IReadOnlyDictionary<BookCandidatePairId, CandidateContentComparison> contentComparisons)
    {
        if (pair.HasAnchor || pair.Contradictions.Any(value => DecisiveContradictions.Contains(value.Code)))
            return false;
        CandidateContentComparison comparison = contentComparisons.GetValueOrDefault(
            pair.Id, EpubContentSignatureComparer.Unavailable());
        return pair.NeedsContentEvidence
            && comparison.Classification is ContentSimilarityClassification.Ambiguous
                or ContentSimilarityClassification.Unavailable;
    }

    private static BibliographicWorkResolution ForBook(
        BibliographicWorkResolution value,
        CalibreBookId bookId) => new(
        bookId,
        value.Provider,
        value.QueryIdentity,
        value.QueryFields,
        value.RetrievedAtUtc,
        value.Status,
        value.WorkId,
        value.ProblemCode);

    private async Task<BibliographicWorkResolution?> TryReadCacheAsync(
        BibliographicLookupQuery query,
        DateTimeOffset minimumRetrievedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return await cache.TryReadAsync(query, minimumRetrievedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return null;
        }
    }

    private async Task<BibliographicSearchResult> SearchAsync(
        BibliographicLookupQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new(
                provider.Identity,
                BibliographicSearchStatus.Unavailable,
                clock.GetUtcNow(),
                problemCode: "BIBLIOGRAPHIC_PROVIDER.UNAVAILABLE");
        }
    }

    private async Task TryWriteCacheAsync(
        BibliographicLookupQuery query,
        BibliographicWorkResolution resolution,
        CancellationToken cancellationToken)
    {
        try
        {
            await cache.WriteAsync(query, resolution, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }
    }

    private async Task TryPruneCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            await cache.PruneAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or HttpRequestException
        or InvalidOperationException
        or ArgumentException
        or OverflowException;
}
