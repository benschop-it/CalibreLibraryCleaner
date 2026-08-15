using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.LocalModels;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.LocalModels;

public sealed class FileLocalEmbeddingComparisonCacheTests
{
    private static readonly LocalEmbeddingModelIdentity Model = new(
        "ollama", "0.32.13", "embeddinggemma:latest", "sha256-test", 128);

    [Fact]
    public async Task RoundTripStoresNoRawInputOrVector()
    {
        using TemporaryDirectory directory = new();
        FileLocalEmbeddingComparisonCache cache = new(new(directory.Path));
        LocalEmbeddingComparisonCacheKey key = Key();
        LocalEmbeddingComparison comparison = new(
            key.PairId, key.Model, key.FirstInputIdentity, key.SecondInputIdentity,
            LocalEmbeddingComparisonStatus.Available, 847);

        await cache.WriteAsync(key, comparison, CancellationToken.None);
        LocalEmbeddingComparison? loaded = await cache.TryReadAsync(key, CancellationToken.None);

        loaded.Should().Be(comparison);
        string path = Directory.GetFiles(directory.Path, "*.json").Single();
        Path.GetFileNameWithoutExtension(path).Should().Be(key.Value);
        string json = await File.ReadAllTextAsync(path);
        json.Should().NotContain("Clockwork Harbor");
        json.Should().NotContain("embedding\"");
    }

    [Fact]
    public async Task CorruptEntryIsCacheMiss()
    {
        using TemporaryDirectory directory = new();
        Directory.CreateDirectory(directory.Path);
        LocalEmbeddingComparisonCacheKey key = Key();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, key.Value + ".json"), "invalid");
        FileLocalEmbeddingComparisonCache cache = new(new(directory.Path));

        LocalEmbeddingComparison? loaded = await cache.TryReadAsync(key, CancellationToken.None);

        loaded.Should().BeNull();
    }

    private static LocalEmbeddingComparisonCacheKey Key() => new(
        new(new(1), new(2)), Model, new string('a', 64), new string('b', 64));
}
