using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Matching;

public sealed class LocalEmbeddingObservationTests
{
    private static readonly LocalEmbeddingModelIdentity Model = new(
        "ollama", "0.32.13", "embeddinggemma:latest", "sha256-model", 128);

    [Fact]
    public async Task ColdRunEmbedsAndWarmRunUsesReducedComparisonCache()
    {
        CalibreBook[] books =
        [
            Book(1, "Clockwork Harbor", "Clara Maker"),
            Book(2, "Clockwork Harbor Annotated", "C. Maker"),
        ];
        BookMatchingProfile[] profiles = BookMatchingProfileFactory.Create(books).ToArray();
        BookCandidatePair pair = Pair();
        RecordingProvider provider = new();
        MemoryCache cache = new();
        ObserveLocalEmbeddingEvidenceUseCase useCase = new(provider, cache, Model);

        LocalEmbeddingObservationResult cold = await useCase.ExecuteAsync(
            books, profiles, [pair], null, CancellationToken.None);
        LocalEmbeddingObservationResult warm = await useCase.ExecuteAsync(
            books, profiles, [pair], null, CancellationToken.None);

        cold.Comparisons.Should().ContainKey(pair.Id);
        cold.ModelBatches.Should().Be(1);
        cold.CacheHits.Should().Be(0);
        warm.CacheHits.Should().Be(1);
        warm.ModelBatches.Should().Be(0);
        provider.BatchCount.Should().Be(1);
        pair.HasAnchor.Should().BeFalse("observations must not alter matching decisions");
    }

    [Fact]
    public async Task DisabledOrUnavailableProviderLeavesNoObservations()
    {
        CalibreBook[] books = [Book(1, "Work", "Author"), Book(2, "Work Revised", "Author")];
        BookMatchingProfile[] profiles = BookMatchingProfileFactory.Create(books).ToArray();
        ObserveLocalEmbeddingEvidenceUseCase disabled = new(
            new RecordingProvider(enabled: false), new MemoryCache(), Model);
        ObserveLocalEmbeddingEvidenceUseCase unavailable = new(
            new RecordingProvider(unavailable: true), new MemoryCache(), Model);

        LocalEmbeddingObservationResult disabledResult = await disabled.ExecuteAsync(
            books, profiles, [Pair()], null, CancellationToken.None);
        LocalEmbeddingObservationResult unavailableResult = await unavailable.ExecuteAsync(
            books, profiles, [Pair()], null, CancellationToken.None);

        disabledResult.Enabled.Should().BeFalse();
        disabledResult.Comparisons.Should().BeEmpty();
        unavailableResult.Enabled.Should().BeTrue();
        unavailableResult.Comparisons.Should().BeEmpty();
        unavailableResult.Failures.Should().Be(2);
    }

    private static CalibreBook Book(long id, string title, string author) => new(
        new(id), title, author, [new(null, author, author)], [], [], $"Book {id}",
        new(languages: ["eng"]));

    private static BookCandidatePair Pair() => new(
        new(new(1), new(2)), 900,
        [new("MATCH.TITLE.TOKEN_MEDIUM", CandidateEvidenceStrength.Supporting)], [], true);

    private sealed class RecordingProvider(bool enabled = true, bool unavailable = false) : ILocalEmbeddingProvider
    {
        public bool Enabled => enabled;
        public int BatchCount { get; private set; }

        public Task<LocalEmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<LocalEmbeddingInput> inputs,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BatchCount++;
            if (unavailable)
                return Task.FromResult(new LocalEmbeddingBatchResult(
                    LocalEmbeddingProviderStatus.Unavailable, null, problemCode: "OLLAMA.UNAVAILABLE"));
            Dictionary<CalibreBookId, LocalEmbeddingVector> vectors = inputs.ToDictionary(
                value => value.BookId,
                value => new LocalEmbeddingVector(
                    value.BookId,
                    value.InputIdentity,
                    Model,
                    Enumerable.Repeat(value.BookId.Value == 1 ? 0.1f : 0.11f, Model.Dimensions)));
            return Task.FromResult(new LocalEmbeddingBatchResult(
                LocalEmbeddingProviderStatus.Available, Model, vectors));
        }
    }

    private sealed class MemoryCache : ILocalEmbeddingComparisonCache
    {
        private readonly Dictionary<string, LocalEmbeddingComparison> _values = new(StringComparer.Ordinal);

        public Task<LocalEmbeddingComparison?> TryReadAsync(
            LocalEmbeddingComparisonCacheKey key,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.TryGetValue(key.Value, out LocalEmbeddingComparison? value);
            return Task.FromResult(value);
        }

        public Task WriteAsync(
            LocalEmbeddingComparisonCacheKey key,
            LocalEmbeddingComparison comparison,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[key.Value] = comparison;
            return Task.CompletedTask;
        }

        public Task PruneAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
