using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Application.Matching;

public enum LocalEmbeddingProviderStatus
{
    Available,
    Disabled,
    Unavailable,
}

public sealed record LocalEmbeddingBatchResult
{
    public LocalEmbeddingBatchResult(
        LocalEmbeddingProviderStatus status,
        LocalEmbeddingModelIdentity? model,
        IReadOnlyDictionary<CalibreBookId, LocalEmbeddingVector>? vectors = null,
        string? problemCode = null)
    {
        LocalEmbeddingVector[] values = (vectors ?? new Dictionary<CalibreBookId, LocalEmbeddingVector>())
            .Values.ToArray();
        if (!Enum.IsDefined(status)
            || (status == LocalEmbeddingProviderStatus.Available) != (model is not null)
            || status != LocalEmbeddingProviderStatus.Available && values.Length > 0
            || model is not null && values.Any(value => value.Model != model)
            || problemCode is { Length: > 128 })
            throw new ArgumentException("Local embedding batch result is invalid.");
        Status = status;
        Model = model;
        Vectors = new ReadOnlyDictionary<CalibreBookId, LocalEmbeddingVector>(
            values.OrderBy(value => value.BookId.Value).ToDictionary(value => value.BookId));
        ProblemCode = string.IsNullOrWhiteSpace(problemCode) ? null : problemCode.Trim();
    }

    public LocalEmbeddingProviderStatus Status { get; }
    public LocalEmbeddingModelIdentity? Model { get; }
    public IReadOnlyDictionary<CalibreBookId, LocalEmbeddingVector> Vectors { get; }
    public string? ProblemCode { get; }
}

public interface ILocalEmbeddingProvider
{
    bool Enabled { get; }

    Task<LocalEmbeddingBatchResult> EmbedAsync(
        IReadOnlyList<LocalEmbeddingInput> inputs,
        CancellationToken cancellationToken);
}

public interface ILocalEmbeddingComparisonCache
{
    Task<LocalEmbeddingComparison?> TryReadAsync(
        LocalEmbeddingComparisonCacheKey key,
        CancellationToken cancellationToken);

    Task WriteAsync(
        LocalEmbeddingComparisonCacheKey key,
        LocalEmbeddingComparison comparison,
        CancellationToken cancellationToken);

    Task PruneAsync(CancellationToken cancellationToken);
}

public sealed record LocalEmbeddingObservationProgress(
    int CompletedPairs,
    int TotalPairs,
    int CacheHits,
    int ModelBatches,
    int AvailableComparisons,
    int Failures,
    string Detail);

public sealed record LocalEmbeddingObservationResult(
    IReadOnlyDictionary<BookCandidatePairId, LocalEmbeddingComparison> Comparisons,
    int PairCount,
    int CacheHits,
    int ModelBatches,
    int EmbeddedRecords,
    int Failures,
    bool InputLimitReached,
    bool Enabled);

public sealed class ObserveLocalEmbeddingEvidenceUseCase(
    ILocalEmbeddingProvider provider,
    ILocalEmbeddingComparisonCache cache,
    LocalEmbeddingModelIdentity? configuredModel)
{
    private const int MaximumInputsPerRun = 24;
    private const int MaximumBatchSize = 8;

    public async Task<LocalEmbeddingObservationResult> ExecuteAsync(
        IReadOnlyList<CalibreBook> books,
        IReadOnlyList<BookMatchingProfile> profiles,
        IReadOnlyList<BookCandidatePair> pairs,
        IProgress<LocalEmbeddingObservationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(books);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(pairs);
        if (!provider.Enabled || configuredModel is null)
            return new(new Dictionary<BookCandidatePairId, LocalEmbeddingComparison>(), 0, 0, 0, 0, 0, false, false);
        BookCandidatePair[] targets = pairs.Where(value => !value.HasAnchor && value.NeedsContentEvidence)
            .OrderBy(value => value.Id.First.Value).ThenBy(value => value.Id.Second.Value).ToArray();
        Dictionary<CalibreBookId, CalibreBook> booksById = books.ToDictionary(value => value.Id);
        Dictionary<CalibreBookId, BookMatchingProfile> profilesById = profiles.ToDictionary(value => value.BookId);
        CalibreBookId[] targetIds = targets.SelectMany(value => new[] { value.Id.First, value.Id.Second })
            .Distinct().OrderBy(value => value.Value).ToArray();
        bool limitReached = targetIds.Length > MaximumInputsPerRun;
        HashSet<CalibreBookId> allowed = targetIds.Take(MaximumInputsPerRun).ToHashSet();
        targets = targets.Where(value => allowed.Contains(value.Id.First) && allowed.Contains(value.Id.Second)).ToArray();
        Dictionary<CalibreBookId, LocalEmbeddingInput> inputs = allowed
            .Where(value => booksById.ContainsKey(value) && profilesById.ContainsKey(value))
            .ToDictionary(value => value, value => LocalEmbeddingInput.Create(booksById[value], profilesById[value]));
        Dictionary<BookCandidatePairId, LocalEmbeddingComparison> comparisons = [];
        List<BookCandidatePair> misses = [];
        int cacheHits = 0;
        int completed = 0;
        progress?.Report(new(0, targets.Length, 0, 0, 0, 0,
            "Local Ollama metadata embeddings enabled; inputs remain on this machine."));
        foreach (BookCandidatePair pair in targets)
        {
            LocalEmbeddingComparisonCacheKey key = LocalEmbeddingComparisonCacheKey.Create(
                pair.Id, configuredModel, inputs);
            LocalEmbeddingComparison? cached = await TryReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (cached is null) misses.Add(pair);
            else
            {
                comparisons[pair.Id] = cached;
                cacheHits++;
                completed++;
            }
        }

        HashSet<CalibreBookId> missingIds = misses.SelectMany(value => new[] { value.Id.First, value.Id.Second })
            .ToHashSet();
        Dictionary<CalibreBookId, LocalEmbeddingVector> vectors = [];
        int batches = 0;
        int failures = 0;
        foreach (LocalEmbeddingInput[] batch in missingIds.OrderBy(value => value.Value)
            .Select(value => inputs[value]).Chunk(MaximumBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            batches++;
            LocalEmbeddingBatchResult result = await EmbedAsync(batch, cancellationToken).ConfigureAwait(false);
            if (result.Status != LocalEmbeddingProviderStatus.Available || result.Model != configuredModel)
            {
                failures += batch.Length;
                continue;
            }
            foreach ((CalibreBookId id, LocalEmbeddingVector vector) in result.Vectors) vectors[id] = vector;
        }

        foreach (BookCandidatePair pair in misses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (vectors.TryGetValue(pair.Id.First, out LocalEmbeddingVector? first)
                && vectors.TryGetValue(pair.Id.Second, out LocalEmbeddingVector? second))
            {
                LocalEmbeddingComparison comparison = LocalEmbeddingComparisonPolicy.Compare(first, second);
                comparisons[pair.Id] = comparison;
                LocalEmbeddingComparisonCacheKey key = LocalEmbeddingComparisonCacheKey.Create(
                    pair.Id, configuredModel, inputs);
                await TryWriteAsync(key, comparison, cancellationToken).ConfigureAwait(false);
            }
            completed++;
            progress?.Report(new(completed, targets.Length, cacheHits, batches, comparisons.Count, failures,
                limitReached ? "Local embedding input limit reached; remaining pairs were not evaluated."
                    : "Comparing local metadata embeddings."));
        }
        await TryPruneAsync(cancellationToken).ConfigureAwait(false);
        return new(comparisons, targets.Length, cacheHits, batches, vectors.Count, failures, limitReached, true);
    }

    private async Task<LocalEmbeddingBatchResult> EmbedAsync(
        IReadOnlyList<LocalEmbeddingInput> inputs,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.EmbedAsync(inputs, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return new(LocalEmbeddingProviderStatus.Unavailable, null, problemCode: "LOCAL_MODEL.UNAVAILABLE");
        }
    }

    private async Task<LocalEmbeddingComparison?> TryReadAsync(
        LocalEmbeddingComparisonCacheKey key,
        CancellationToken cancellationToken)
    {
        try { return await cache.TryReadAsync(key, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsRecoverable(exception)) { return null; }
    }

    private async Task TryWriteAsync(
        LocalEmbeddingComparisonCacheKey key,
        LocalEmbeddingComparison comparison,
        CancellationToken cancellationToken)
    {
        try { await cache.WriteAsync(key, comparison, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsRecoverable(exception)) { }
    }

    private async Task TryPruneAsync(CancellationToken cancellationToken)
    {
        try { await cache.PruneAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (IsRecoverable(exception)) { }
    }

    private static bool IsRecoverable(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InvalidDataException or HttpRequestException
        or InvalidOperationException or ArgumentException or OverflowException;
}
