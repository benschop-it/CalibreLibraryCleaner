using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Infrastructure.Metadata;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Metadata;

public sealed class FileEmbeddedAuthorMetadataCacheTests
{
    [Fact]
    public async Task ExactIdentityRoundTripsWithoutPathsOrProse()
    {
        using TemporaryDirectory directory = new();
        FileEmbeddedAuthorMetadataCache cache = new(new(directory.Path));
        EmbeddedAuthorMetadataCacheKey key = Key('a', "extractor/1");
        EmbeddedAuthorMetadataObservation observation = new(
            key, EmbeddedAuthorMetadataStatus.Available, "Title", ["Writer"]);

        await cache.WriteAsync(observation, CancellationToken.None);
        EmbeddedAuthorMetadataObservation? loaded = await cache.TryReadAsync(key, CancellationToken.None);

        loaded.Should().BeEquivalentTo(observation);
        string json = await File.ReadAllTextAsync(Directory.GetFiles(directory.Path, "*.json").Single());
        json.Should().NotContain(directory.Path);
    }

    [Fact]
    public async Task VersionChangeAndCorruptionBecomeMisses()
    {
        using TemporaryDirectory directory = new();
        FileEmbeddedAuthorMetadataCache cache = new(new(directory.Path));
        EmbeddedAuthorMetadataCacheKey key = Key('a', "extractor/1");
        await cache.WriteAsync(
            new EmbeddedAuthorMetadataObservation(
                key, EmbeddedAuthorMetadataStatus.Missing, "Title", []),
            CancellationToken.None);

        (await cache.TryReadAsync(Key('a', "extractor/2"), CancellationToken.None))
            .Should().BeNull();
        await File.WriteAllTextAsync(Directory.GetFiles(directory.Path, "*.json").Single(), "not-json");
        (await cache.TryReadAsync(key, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task EntryCountIsBounded()
    {
        using TemporaryDirectory directory = new();
        FileEmbeddedAuthorMetadataCache cache = new(new(
            directory.Path, maximumEntryBytes: 4_096, maximumTotalBytes: 8_192,
            maximumEntries: 2));
        foreach (char value in new[] { 'a', 'b', 'c' })
        {
            EmbeddedAuthorMetadataCacheKey key = Key(value, "extractor/1");
            await cache.WriteAsync(
                new EmbeddedAuthorMetadataObservation(
                    key, EmbeddedAuthorMetadataStatus.Available, null, ["Writer"]),
                CancellationToken.None);
        }

        Directory.GetFiles(directory.Path, "*.json").Should().HaveCount(2);
    }

    [Fact]
    public async Task FrontMatterFactsRoundTripWithoutProseOrPaths()
    {
        using TemporaryDirectory directory = new();
        FileEmbeddedAuthorMetadataCache cache = new(new(directory.Path));
        FormatFileFingerprint fingerprint = new(10, new(new string('d', 64)));
        FrontMatterAuthorObservation observation = new(
            "PDF",
            fingerprint,
            FrontMatterAuthorVersions.PdfExtractor,
            FrontMatterAuthorVersions.Limits,
            FrontMatterAuthorStatus.Available,
            [new(AuthorCreditRole.Author, ["Reliable Writer"], 1, 1, "By")],
            5,
            5,
            100,
            false);

        await cache.WriteAsync(observation, CancellationToken.None);
        FrontMatterAuthorObservation? loaded = await cache.TryReadAsync(
            new FrontMatterAuthorCacheKey(
                "PDF", fingerprint, FrontMatterAuthorVersions.PdfExtractor,
                FrontMatterAuthorVersions.Limits),
            CancellationToken.None);

        loaded.Should().BeEquivalentTo(observation);
        string json = await File.ReadAllTextAsync(Directory.GetFiles(directory.Path, "*.json").Single());
        json.Should().NotContain(directory.Path).And.NotContain("page text");
    }

    [Fact]
    public async Task FrontMatterVersionChangeAndCorruptionBecomeMisses()
    {
        using TemporaryDirectory directory = new();
        FileEmbeddedAuthorMetadataCache cache = new(new(directory.Path));
        FormatFileFingerprint fingerprint = new(10, new(new string('e', 64)));
        FrontMatterAuthorObservation observation = new(
            "EPUB", fingerprint, "front/1", "limits/1",
            FrontMatterAuthorStatus.Missing, [], 3, 3, 100, false);
        await cache.WriteAsync(observation, CancellationToken.None);

        (await cache.TryReadAsync(
            new FrontMatterAuthorCacheKey(
                "EPUB", fingerprint, "front/2", "limits/1"), CancellationToken.None))
            .Should().BeNull();
        await File.WriteAllTextAsync(Directory.GetFiles(directory.Path, "*.json").Single(), "not-json");
        (await cache.TryReadAsync(
            new FrontMatterAuthorCacheKey(
                "EPUB", fingerprint, "front/1", "limits/1"), CancellationToken.None))
            .Should().BeNull();
    }

    private static EmbeddedAuthorMetadataCacheKey Key(char hash, string extractor) => new(
        "EPUB",
        new(10, new(new string(hash, 64))),
        extractor,
        "limits/1");
}
