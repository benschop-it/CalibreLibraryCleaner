using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;

namespace CalibreLibraryCleaner.Application.Metadata;

public interface IEditionMetadataProvider
{
    EditionMetadataProviderIdentity Identity { get; }

    Task<EditionMetadataSearchResult> SearchAsync(
        EditionMetadataLookupQuery query,
        CancellationToken cancellationToken);
}

public interface IEditionMetadataProviderAvailability
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
}

public interface IEditionMetadataProposalCache
{
    Task<EditionMetadataProposal?> TryReadAsync(
        EditionMetadataLookupQuery query,
        DateTimeOffset minimumRetrievedAtUtc,
        CancellationToken cancellationToken);

    Task WriteAsync(
        EditionMetadataLookupQuery query,
        EditionMetadataProposal proposal,
        CancellationToken cancellationToken);

    Task PruneAsync(CancellationToken cancellationToken);
}

public sealed record EditionMetadataEnrichmentOptions
{
    public EditionMetadataEnrichmentOptions(
        bool enabled = true,
        int maximumProviderRequestsPerRun = 10_000,
        TimeSpan? maximumCacheAge = null,
        TimeSpan? unavailableCacheAge = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumProviderRequestsPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumProviderRequestsPerRun, 50_000);
        TimeSpan age = maximumCacheAge ?? TimeSpan.FromDays(30);
        if (age < TimeSpan.FromHours(1) || age > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(maximumCacheAge));
        TimeSpan unavailableAge = unavailableCacheAge ?? TimeSpan.FromHours(6);
        if (unavailableAge < TimeSpan.FromMinutes(5) || unavailableAge > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(unavailableCacheAge));
        Enabled = enabled;
        MaximumProviderRequestsPerRun = maximumProviderRequestsPerRun;
        MaximumCacheAge = age;
        UnavailableCacheAge = unavailableAge;
    }

    public bool Enabled { get; }
    public int MaximumProviderRequestsPerRun { get; }
    public TimeSpan MaximumCacheAge { get; }
    public TimeSpan UnavailableCacheAge { get; }
}

public sealed record EditionMetadataEnrichmentProgress(
    EditionMetadataProviderIdentity Provider,
    int CompletedQueries,
    int TotalQueries,
    int CacheHits,
    int ProviderRequests,
    int ProposedRecords,
    int Failures,
    string Detail);

public sealed record EditionMetadataProposalBatchResult(
    EditionMetadataProviderIdentity Provider,
    IReadOnlyDictionary<CalibreBookId, EditionMetadataProposal> Proposals,
    int QueryCount,
    int UnqueryableRecordCount,
    int CacheHits,
    int ProviderRequests,
    int ProposedRecords,
    int Failures,
    bool RequestLimitReached,
    bool Enabled)
{
    public static EditionMetadataProposalBatchResult Disabled(
        EditionMetadataProviderIdentity provider,
        int unqueryableRecordCount) => new(
        provider,
        new Dictionary<CalibreBookId, EditionMetadataProposal>(),
        0,
        unqueryableRecordCount,
        0,
        0,
        0,
        0,
        false,
        false);
}

public sealed record EditionMetadataProvidersBatchResult(
    IReadOnlyList<EditionMetadataProposalBatchResult> Providers);

public sealed class ResolveEditionMetadataProposalsUseCase
{
    private readonly IEditionMetadataProposalCache _cache;
    private readonly IClock _clock;
    private readonly EditionMetadataEnrichmentOptions _options;
    private readonly IReadOnlyList<IEditionMetadataProvider> _providers;

    public ResolveEditionMetadataProposalsUseCase(
        IEnumerable<IEditionMetadataProvider> providers,
        IEditionMetadataProposalCache cache,
        IClock clock,
        EditionMetadataEnrichmentOptions options)
    {
        ArgumentNullException.ThrowIfNull(providers);
        IEditionMetadataProvider[] values = providers
            .OrderBy(value => value.Identity.Id, StringComparer.Ordinal)
            .ThenBy(value => value.Identity.Version, StringComparer.Ordinal).ToArray();
        if (values.Select(value => value.Identity).Distinct().Count() != values.Length)
            throw new ArgumentException("Edition metadata providers must have unique identities.", nameof(providers));
        _providers = values;
        _cache = cache;
        _clock = clock;
        _options = options;
    }

    public async Task<EditionMetadataProvidersBatchResult> ExecuteAsync(
        IReadOnlyList<CalibreBook> books,
        IProgress<EditionMetadataEnrichmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(books);
        List<EditionMetadataProposalBatchResult> results = [];
        foreach (IEditionMetadataProvider provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ExecuteProviderAsync(
                provider, books, progress, cancellationToken).ConfigureAwait(false));
        }
        return new(results);
    }

    private async Task<EditionMetadataProposalBatchResult> ExecuteProviderAsync(
        IEditionMetadataProvider provider,
        IReadOnlyList<CalibreBook> books,
        IProgress<EditionMetadataEnrichmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || provider is IEditionMetadataProviderAvailability availability
            && !await availability.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
            return EditionMetadataProposalBatchResult.Disabled(provider.Identity, 0);
        EditionMetadataLookupQuery[] queries = books.OrderBy(value => value.Id.Value)
            .Select(book => TryCreateQuery(provider, book))
            .Where(value => value is not null).Select(value => value!).ToArray();
        int unqueryable = books.Count - queries.Length;
        IGrouping<QueryKey, EditionMetadataLookupQuery>[] groups = queries
            .GroupBy(QueryKey.Create)
            .OrderBy(value => value.Key.SortKey, StringComparer.Ordinal).ToArray();
        Dictionary<CalibreBookId, EditionMetadataProposal> proposals = [];
        int completed = 0;
        int cacheHits = 0;
        int requests = 0;
        int failures = 0;
        bool limitReached = false;
        DateTimeOffset minimumRetrievedAt = _clock.GetUtcNow().ToUniversalTime() - _options.MaximumCacheAge;
        DateTimeOffset minimumUnavailableRetrievedAt = _clock.GetUtcNow().ToUniversalTime()
            - _options.UnavailableCacheAge;
        progress?.Report(new(provider.Identity, 0, groups.Length, 0, 0, 0, 0,
            "Online edition metadata lookup enabled; transmitting ISBN or title, author, and optional language fields."));

        foreach (IGrouping<QueryKey, EditionMetadataLookupQuery> group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EditionMetadataLookupQuery representative = group.First();
            EditionMetadataProposal? proposal = await TryReadCacheAsync(
                representative, minimumRetrievedAt, cancellationToken).ConfigureAwait(false);
            if (proposal?.Status == EditionMetadataProposalStatus.Unavailable
                && proposal.RetrievedAtUtc < minimumUnavailableRetrievedAt)
                proposal = null;
            if (proposal is not null)
            {
                cacheHits++;
            }
            else if (requests >= _options.MaximumProviderRequestsPerRun)
            {
                limitReached = true;
                progress?.Report(new(
                    provider.Identity,
                    completed,
                    groups.Length,
                    cacheHits,
                    requests,
                    proposals.Values.Count(value => value.Status == EditionMetadataProposalStatus.Proposed),
                    failures,
                    $"Online request limit reached; {groups.Length - completed:N0} uncached queries deferred."));
                break;
            }
            else
            {
                requests++;
                EditionMetadataSearchResult search = await SearchAsync(
                    provider, representative, cancellationToken).ConfigureAwait(false);
                proposal = EditionMetadataProposalPolicy.Resolve(representative, search);
                if (proposal.Status == EditionMetadataProposalStatus.Unavailable) failures++;
                await TryWriteCacheAsync(representative, proposal, cancellationToken).ConfigureAwait(false);
            }

            if (proposal is not null)
            {
                foreach (EditionMetadataLookupQuery query in group)
                    proposals[query.BookId] = ForBook(proposal, query.BookId);
            }
            completed++;
            progress?.Report(new(
                provider.Identity,
                completed,
                groups.Length,
                cacheHits,
                requests,
                proposals.Values.Count(value => value.Status == EditionMetadataProposalStatus.Proposed),
                failures,
                "Resolving bounded online edition metadata."));
        }

        await TryPruneCacheAsync(cancellationToken).ConfigureAwait(false);
        return new(
            provider.Identity,
            proposals,
            groups.Length,
            unqueryable,
            cacheHits,
            requests,
            proposals.Values.Count(value => value.Status == EditionMetadataProposalStatus.Proposed),
            failures,
            limitReached,
            true);
    }

    private static EditionMetadataLookupQuery? TryCreateQuery(
        IEditionMetadataProvider provider,
        CalibreBook book)
    {
        try
        {
            return EditionMetadataLookupQuery.Create(book, provider.Identity);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<EditionMetadataProposal?> TryReadCacheAsync(
        EditionMetadataLookupQuery query,
        DateTimeOffset minimumRetrievedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _cache.TryReadAsync(query, minimumRetrievedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsRecoverable(exception)) { return null; }
    }

    private async Task<EditionMetadataSearchResult> SearchAsync(
        IEditionMetadataProvider provider,
        EditionMetadataLookupQuery query,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new(
                provider.Identity,
                EditionMetadataSearchStatus.Unavailable,
                _clock.GetUtcNow(),
                problemCode: "EDITION_METADATA_PROVIDER.UNAVAILABLE");
        }
    }

    private async Task TryWriteCacheAsync(
        EditionMetadataLookupQuery query,
        EditionMetadataProposal proposal,
        CancellationToken cancellationToken)
    {
        try
        {
            await _cache.WriteAsync(query, proposal, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsRecoverable(exception)) { }
    }

    private async Task TryPruneCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _cache.PruneAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsRecoverable(exception)) { }
    }

    private static EditionMetadataProposal ForBook(
        EditionMetadataProposal value,
        CalibreBookId bookId) => new(
        bookId,
        value.Provider,
        value.QueryFields,
        value.RetrievedAtUtc,
        value.Status,
        value.Candidate,
        value.MatchScore,
        value.ReasonCodes,
        value.ProblemCode);

    private static bool IsRecoverable(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or HttpRequestException
        or InvalidOperationException
        or ArgumentException
        or OverflowException;

    private sealed record QueryKey(
        EditionMetadataProviderIdentity Provider,
        EditionMetadataQueryFields Fields,
        string? Identifier,
        string? Title,
        string Authors,
        string? Language)
    {
        public string SortKey => Identifier ?? string.Join('|', Title, Authors, Language);

        public static QueryKey Create(EditionMetadataLookupQuery value) => new(
            value.Provider,
            value.Fields,
            value.Identifier,
            value.Title,
            string.Join('\u001f', value.Authors),
            value.Language);
    }
}
