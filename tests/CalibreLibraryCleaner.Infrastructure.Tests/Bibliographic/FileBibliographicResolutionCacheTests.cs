using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.Bibliographic;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Bibliographic;

public sealed class FileBibliographicResolutionCacheTests
{
    private static readonly BibliographicProviderIdentity Provider = new(
        "open-library", "open-library-search-1.0.0");
    private static readonly DateTimeOffset RetrievedAt = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RoundTripUsesQueryHashAndStoresNoRawQueryMetadata()
    {
        using TemporaryDirectory directory = new();
        FileBibliographicResolutionCache cache = new(new(directory.Path));
        BibliographicLookupQuery first = Query(1);
        BibliographicWorkResolution resolution = new(
            first.BookId,
            Provider,
            first.QueryIdentity,
            first.Fields,
            RetrievedAt,
            BibliographicResolutionStatus.Matched,
            "/works/OL138052W");

        await cache.WriteAsync(first, resolution, CancellationToken.None);
        BibliographicWorkResolution? loaded = await cache.TryReadAsync(
            Query(2), RetrievedAt.AddDays(-1), CancellationToken.None);

        loaded!.BookId.Should().Be(new CalibreBookId(2));
        loaded.WorkId.Should().Be("/works/OL138052W");
        string[] files = Directory.GetFiles(directory.Path, "*.json");
        files.Should().ContainSingle();
        Path.GetFileNameWithoutExtension(files[0]).Should().Be(first.QueryIdentity);
        string json = await File.ReadAllTextAsync(files[0]);
        json.Should().NotContain("Private Title");
        json.Should().NotContain("Private Author");
    }

    [Fact]
    public async Task StaleAndCorruptEntriesBecomeMisses()
    {
        using TemporaryDirectory directory = new();
        FileBibliographicResolutionCache cache = new(new(directory.Path));
        BibliographicLookupQuery query = Query(1);
        BibliographicWorkResolution resolution = new(
            query.BookId,
            Provider,
            query.QueryIdentity,
            query.Fields,
            RetrievedAt,
            BibliographicResolutionStatus.NotFound,
            problemCode: "OPEN_LIBRARY.NO_COMPATIBLE_RESULTS");
        await cache.WriteAsync(query, resolution, CancellationToken.None);

        BibliographicWorkResolution? stale = await cache.TryReadAsync(
            query, RetrievedAt.AddMinutes(1), CancellationToken.None);
        await File.WriteAllTextAsync(Directory.GetFiles(directory.Path, "*.json").Single(), "not-json");
        BibliographicWorkResolution? corrupt = await cache.TryReadAsync(
            query, RetrievedAt.AddDays(-1), CancellationToken.None);

        stale.Should().BeNull();
        corrupt.Should().BeNull();
    }

    [Fact]
    public async Task ResolutionPolicyVersionMismatchBecomesMiss()
    {
        using TemporaryDirectory directory = new();
        FileBibliographicResolutionCache cache = new(new(directory.Path));
        BibliographicLookupQuery query = Query(1);
        BibliographicWorkResolution resolution = new(
            query.BookId,
            Provider,
            query.QueryIdentity,
            query.Fields,
            RetrievedAt,
            BibliographicResolutionStatus.Matched,
            "/works/OL138052W");
        await cache.WriteAsync(query, resolution, CancellationToken.None);
        string path = Directory.GetFiles(directory.Path, "*.json").Single();
        string json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace(
            BibliographicWorkResolution.PolicyVersion,
            "bibliographic-resolution/0.0.0",
            StringComparison.Ordinal));

        BibliographicWorkResolution? loaded = await cache.TryReadAsync(
            query, RetrievedAt.AddDays(-1), CancellationToken.None);

        loaded.Should().BeNull();
    }

    private static BibliographicLookupQuery Query(long id) => new(
        new(id),
        Provider,
        BibliographicQueryFields.Title | BibliographicQueryFields.Authors | BibliographicQueryFields.Language,
        null,
        "Private Title",
        ["Private Author"],
        "en");
}
