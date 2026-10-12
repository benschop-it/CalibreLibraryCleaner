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

public interface IBudgetedEditionMetadataProvider
{
    Task<EditionMetadataSearchExecution> SearchAsync(
        EditionMetadataLookupQuery query,
        int maximumProviderRequests,
        CancellationToken cancellationToken);
}

public sealed record EditionMetadataSearchExecution
{
    public EditionMetadataSearchExecution(
        EditionMetadataSearchResult result,
        int providerRequests)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentOutOfRangeException.ThrowIfNegative(providerRequests);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(providerRequests, 10);
        Result = result;
        ProviderRequests = providerRequests;
    }

    public EditionMetadataSearchResult Result { get; }
    public int ProviderRequests { get; }
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

public interface IEditionMetadataProposalCacheMaintenance
{
    Task PrepareIdentifierAliasesAsync(
        EditionMetadataProviderIdentity provider,
        DateTimeOffset minimumRetrievedAtUtc,
        CancellationToken cancellationToken);
}

public sealed record EditionMetadataEnrichmentOptions
{
    public EditionMetadataEnrichmentOptions(
        bool enabled = true,
        int maximumProviderRequestsPerRun = 10_000,
        int maximumGoogleBooksRequestsPerRun = 200,
        TimeSpan? maximumCacheAge = null,
        TimeSpan? unavailableCacheAge = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumProviderRequestsPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumProviderRequestsPerRun, 50_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumGoogleBooksRequestsPerRun, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumGoogleBooksRequestsPerRun, 50_000);
        TimeSpan age = maximumCacheAge ?? TimeSpan.FromDays(365);
        if (age < TimeSpan.FromHours(1) || age > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(maximumCacheAge));
        TimeSpan unavailableAge = unavailableCacheAge ?? TimeSpan.FromHours(6);
        if (unavailableAge < TimeSpan.FromMinutes(5) || unavailableAge > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(unavailableCacheAge));
        Enabled = enabled;
        MaximumProviderRequestsPerRun = maximumProviderRequestsPerRun;
        MaximumGoogleBooksRequestsPerRun = maximumGoogleBooksRequestsPerRun;
        MaximumCacheAge = age;
        UnavailableCacheAge = unavailableAge;
    }

    public bool Enabled { get; }
    public int MaximumProviderRequestsPerRun { get; }
    public int MaximumGoogleBooksRequestsPerRun { get; }
    public TimeSpan MaximumCacheAge { get; }
    public TimeSpan UnavailableCacheAge { get; }

    public int MaximumRequestsFor(EditionMetadataProviderIdentity provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return provider.Id == "google-books"
            ? MaximumGoogleBooksRequestsPerRun
            : MaximumProviderRequestsPerRun;
    }
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
    int CompletedQueryCount,
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
        QueryPlan[] queries = books.OrderBy(value => value.Id.Value)
            .Select(book => TryCreateQueryPlan(provider, book))
            .Where(value => value is not null).Select(value => value!).ToArray();
        int unqueryable = books.Count - queries.Length;
        IGrouping<QueryPlanKey, QueryPlan>[] groups = queries
            .GroupBy(QueryPlanKey.Create)
            .OrderBy(value => value.Key.SortKey, StringComparer.Ordinal).ToArray();
        Dictionary<CalibreBookId, EditionMetadataProposal> proposals = [];
        int completed = 0;
        int cacheHits = 0;
        int requests = 0;
        int failures = 0;
        bool limitReached = false;
        int maximumProviderRequests = _options.MaximumRequestsFor(provider.Identity);
        DateTimeOffset minimumRetrievedAt = _clock.GetUtcNow().ToUniversalTime() - _options.MaximumCacheAge;
        DateTimeOffset minimumUnavailableRetrievedAt = _clock.GetUtcNow().ToUniversalTime()
            - _options.UnavailableCacheAge;
        progress?.Report(new(provider.Identity, 0, groups.Length, 0, 0, 0, 0,
            "Preparing reusable global edition metadata cache entries."));
        await TryPrepareIdentifierAliasesAsync(
            provider.Identity, minimumRetrievedAt, cancellationToken).ConfigureAwait(false);
        progress?.Report(new(provider.Identity, 0, groups.Length, 0, 0, 0, 0,
            "Online edition metadata lookup enabled; transmitting ISBN or title, author, and optional language fields."));

        foreach (IGrouping<QueryPlanKey, QueryPlan> group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueryPlan representative = group.First();
            EditionMetadataProposal? proposal = null;
            bool deferred = false;
            foreach (EditionMetadataLookupQuery variant in representative.Variants)
            {
                EditionMetadataProposal? variantProposal = await TryReadCacheAsync(
                    variant, minimumRetrievedAt, cancellationToken).ConfigureAwait(false);
                if (variantProposal?.Status == EditionMetadataProposalStatus.Unavailable
                    && variantProposal.RetrievedAtUtc < minimumUnavailableRetrievedAt)
                    variantProposal = null;
                if (variantProposal is not null)
                {
                    cacheHits++;
                    proposal = variantProposal;
                }
                else if (requests >= maximumProviderRequests)
                {
                    limitReached = true;
                    deferred = proposal is null;
                    progress?.Report(new(
                        provider.Identity,
                        completed,
                        groups.Length,
                        cacheHits,
                        requests,
                        proposals.Values.Count(value => value.Status == EditionMetadataProposalStatus.Proposed),
                        failures,
                        $"Online request limit reached; {groups.Length - completed - (proposal is null ? 0 : 1):N0} uncached queries deferred."));
                    break;
                }
                else
                {
                    EditionMetadataSearchExecution execution = await SearchAsync(
                        provider,
                        variant,
                        maximumProviderRequests - requests,
                        cancellationToken).ConfigureAwait(false);
                    requests += execution.ProviderRequests;
                    EditionMetadataLookupQuery validation = variant.Identifier is null
                        ? representative.Validation
                        : variant;
                    proposal = ForQuery(
                        EditionMetadataProposalPolicy.Resolve(validation, execution.Result),
                        variant);
                    if (proposal.Status == EditionMetadataProposalStatus.Unavailable) failures++;
                    await TryWriteCacheAsync(variant, proposal, cancellationToken).ConfigureAwait(false);
                    await TryWriteIdentifierAliasesAsync(proposal, cancellationToken).ConfigureAwait(false);
                }
                if (proposal.Status != EditionMetadataProposalStatus.NotFound) break;
            }
            if (deferred) break;

            if (proposal is not null)
            {
                foreach (QueryPlan query in group)
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
                limitReached
                    ? $"Online request limit reached; {groups.Length - completed:N0} uncached queries deferred."
                    : "Resolving bounded online edition metadata."));
            if (limitReached) break;
        }

        await TryPruneCacheAsync(cancellationToken).ConfigureAwait(false);
        return new(
            provider.Identity,
            proposals,
            groups.Length,
            completed,
            unqueryable,
            cacheHits,
            requests,
            proposals.Values.Count(value => value.Status == EditionMetadataProposalStatus.Proposed),
            failures,
            limitReached,
            true);
    }

    private static QueryPlan? TryCreateQueryPlan(
        IEditionMetadataProvider provider,
        CalibreBook book)
    {
        try
        {
            return new(
                book.Id,
                EditionMetadataLookupQuery.CreateValidation(book, provider.Identity),
                EditionMetadataLookupQuery.CreateVariants(book, provider.Identity));
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

    private async Task<EditionMetadataSearchExecution> SearchAsync(
        IEditionMetadataProvider provider,
        EditionMetadataLookupQuery query,
        int maximumProviderRequests,
        CancellationToken cancellationToken)
    {
        try
        {
            if (provider is IBudgetedEditionMetadataProvider budgeted)
                return await budgeted.SearchAsync(
                    query, maximumProviderRequests, cancellationToken).ConfigureAwait(false);
            EditionMetadataSearchResult result = await provider.SearchAsync(
                query, cancellationToken).ConfigureAwait(false);
            return new(result, 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new(new(
                    provider.Identity,
                    EditionMetadataSearchStatus.Unavailable,
                    _clock.GetUtcNow(),
                    problemCode: "EDITION_METADATA_PROVIDER.UNAVAILABLE"),
                1);
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

    private async Task TryWriteIdentifierAliasesAsync(
        EditionMetadataProposal proposal,
        CancellationToken cancellationToken)
    {
        if (proposal.Status != EditionMetadataProposalStatus.Proposed || proposal.Candidate is null) return;
        EditionMetadataSearchResult search = new(
            proposal.Provider,
            EditionMetadataSearchStatus.Success,
            proposal.RetrievedAtUtc,
            [proposal.Candidate]);
        foreach (EditionMetadataLookupQuery alias in EditionMetadataLookupQuery.CreateIdentifierAliases(
                     proposal.BookId, proposal.Provider, proposal.Candidate))
        {
            EditionMetadataProposal aliasProposal = EditionMetadataProposalPolicy.Resolve(alias, search);
            await TryWriteCacheAsync(alias, aliasProposal, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryPrepareIdentifierAliasesAsync(
        EditionMetadataProviderIdentity provider,
        DateTimeOffset minimumRetrievedAtUtc,
        CancellationToken cancellationToken)
    {
        if (_cache is not IEditionMetadataProposalCacheMaintenance maintenance) return;
        try
        {
            await maintenance.PrepareIdentifierAliasesAsync(
                provider, minimumRetrievedAtUtc, cancellationToken).ConfigureAwait(false);
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

    private static EditionMetadataProposal ForQuery(
        EditionMetadataProposal value,
        EditionMetadataLookupQuery query) => new(
        query.BookId,
        value.Provider,
        query.Fields,
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

    private sealed record QueryPlan(
        CalibreBookId BookId,
        EditionMetadataLookupQuery Validation,
        IReadOnlyList<EditionMetadataLookupQuery> Variants);

    private sealed record QueryPlanKey(string SortKey)
    {
        public static QueryPlanKey Create(QueryPlan value) => new(string.Join('\u001e',
            value.Variants.Select(variant => string.Join('\u001f',
                ((int)variant.Fields).ToString(System.Globalization.CultureInfo.InvariantCulture),
                variant.Identifier,
                variant.Title,
                string.Join('\u001d', variant.Authors),
                variant.Language))));
    }
}
