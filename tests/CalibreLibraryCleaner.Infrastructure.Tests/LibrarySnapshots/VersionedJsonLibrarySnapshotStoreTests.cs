using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;
using CalibreLibraryCleaner.Infrastructure.Tests.Execution;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.LibrarySnapshots;

public sealed class VersionedJsonLibrarySnapshotStoreTests
{
    [Fact]
    public async Task WriteReplacesLatestSnapshotForCanonicalLibraryPath()
    {
        using TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        string cacheRoot = Path.Combine(directory.Path, "cache");
        VersionedJsonLibrarySnapshotStore store = new(new() { StorageRoot = cacheRoot });
        LibrarySnapshot latest = CopyAt(fixture.Snapshot, fixture.Snapshot.ScannedAt.AddMinutes(5));

        await store.WriteAsync(fixture.Snapshot, CancellationToken.None);
        await store.WriteAsync(latest, CancellationToken.None);
        LibrarySnapshot? loaded = await store.ReadAsync(
            fixture.Snapshot.Identity.LibraryRoot + Path.DirectorySeparatorChar,
            CancellationToken.None);
        IReadOnlyList<PersistedLibrarySnapshotInfo> listed =
            await store.ListAsync(CancellationToken.None);

        loaded!.ScannedAt.Should().Be(latest.ScannedAt);
        listed.Should().ContainSingle().Which.Should().Be(new PersistedLibrarySnapshotInfo(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixture.Snapshot.Identity.LibraryRoot)),
            latest.ScannedAt));
        Directory.GetFiles(cacheRoot, "*.library-snapshot.json").Should().ContainSingle();
        Directory.GetFiles(cacheRoot, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task MissingPathHasNoPersistedSnapshot()
    {
        using TemporaryDirectory directory = new();
        VersionedJsonLibrarySnapshotStore store = new(new() { StorageRoot = Path.Combine(directory.Path, "cache") });

        LibrarySnapshot? loaded = await store.ReadAsync(Path.Combine(directory.Path, "library"), CancellationToken.None);

        loaded.Should().BeNull();
        (await store.ListAsync(CancellationToken.None)).Should().BeEmpty();
    }

    private static LibrarySnapshot CopyAt(LibrarySnapshot snapshot, DateTimeOffset scannedAt) => new(
        snapshot.Identity,
        scannedAt,
        snapshot.Books,
        snapshot.Findings,
        snapshot.ExactBinaryDuplicateGroups,
        snapshot.ExactMetadataDuplicateGroups,
        snapshot.EpubAssessments,
        snapshot.ConsolidationRecommendations,
        snapshot.PdfAssessments);
}
