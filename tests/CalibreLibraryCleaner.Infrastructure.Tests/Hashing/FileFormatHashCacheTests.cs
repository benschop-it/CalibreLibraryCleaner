using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Caches;
using CalibreLibraryCleaner.Infrastructure.Hashing;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Hashing;

public sealed class FileFormatHashCacheTests
{
    [Fact]
    public void KeyFactoryIsDeterministicPrivateAndSensitiveToRootAndRelativePath()
    {
        Sha256FormatHashCacheKeyFactory factory = new();
        string firstRoot = Path.GetFullPath(Path.Combine("library", "one"));
        string secondRoot = Path.GetFullPath(Path.Combine("library", "two"));

        FormatHashCacheKey original = factory.Create(Resolved(firstRoot, "private-author/book.epub"));
        FormatHashCacheKey repeated = factory.Create(Resolved(firstRoot, "private-author/book.epub"));
        FormatHashCacheKey differentRoot = factory.Create(Resolved(secondRoot, "private-author/book.epub"));
        FormatHashCacheKey differentPath = factory.Create(Resolved(firstRoot, "private-author/other.epub"));

        original.Should().Be(repeated);
        original.Value.Should().MatchRegex("^[0-9a-f]{64}$");
        original.Value.Should().NotContain("private-author");
        original.Should().NotBe(differentRoot).And.NotBe(differentPath);
    }

    [Fact]
    public async Task EntryRoundTripsWithoutRawPaths()
    {
        using TemporaryDirectory directory = new();
        FileFormatHashCache cache = new(new(directory.Path));
        string libraryRoot = Path.Combine(directory.Path, "private-library-name");
        const string relativePath = "private-author/private-title.epub";
        FormatHashCacheKey key = new Sha256FormatHashCacheKeyFactory().Create(Resolved(libraryRoot, relativePath));
        FormatHashCacheEntry entry = Entry(key);

        await cache.WriteAsync(entry, CancellationToken.None);
        FormatHashCacheEntry? loaded = await cache.TryReadAsync(key, CancellationToken.None);

        loaded.Should().BeEquivalentTo(entry);
        string json = await File.ReadAllTextAsync(Directory.GetFiles(directory.Path, "*.json").Single());
        json.Should().NotContain(libraryRoot).And.NotContain(relativePath).And.NotContain("private-author");
    }

    [Fact]
    public async Task CorruptOrStaleEntryBecomesCacheMiss()
    {
        using TemporaryDirectory directory = new();
        FileFormatHashCache cache = new(new(directory.Path));
        FormatHashCacheKey key = Key(directory.Path, "book.epub");
        await cache.WriteAsync(Entry(key), CancellationToken.None);
        string path = Directory.GetFiles(directory.Path, "*.json").Single();
        string json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace(
            FormatHashCacheKey.PolicyVersion,
            "format-sha256/stale",
            StringComparison.Ordinal));

        FormatHashCacheEntry? loaded = await cache.TryReadAsync(key, CancellationToken.None);

        loaded.Should().BeNull();
        await File.WriteAllTextAsync(path, "{");
        (await cache.TryReadAsync(key, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task PruneKeepsCacheWithinConfiguredBound()
    {
        using TemporaryDirectory directory = new();
        FileFormatHashCache cache = new(new(
            directory.Path,
            maximumEntryBytes: 1024,
            maximumTotalBytes: 1024));
        for (int index = 0; index < 8; index++)
        {
            FormatHashCacheKey key = Key(directory.Path, $"book-{index}.epub");
            await cache.WriteAsync(Entry(key, index), CancellationToken.None);
        }

        await cache.PruneAsync(CancellationToken.None);

        Directory.GetFiles(directory.Path, "*.json")
            .Sum(path => new FileInfo(path).Length)
            .Should().BeLessThanOrEqualTo(1024);
    }

    private static FormatHashCacheKey Key(string root, string relativePath) =>
        new Sha256FormatHashCacheKeyFactory().Create(Resolved(root, relativePath));

    private static ResolvedFormatPath Resolved(string root, string relativePath) =>
        new(root, Path.Combine(root, relativePath), relativePath);

    private static FormatHashCacheEntry Entry(FormatHashCacheKey key, int seconds = 0)
    {
        DateTimeOffset timestamp = DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        FormatFileObservation observation = new(1024, timestamp, timestamp, (int)FileAttributes.Archive);
        return new(
            key,
            FormatHashCacheKey.PolicyVersion,
            new(1024, new(new string("0123456789abcdef"[seconds % 16], 64))),
            observation,
            timestamp);
    }
}
