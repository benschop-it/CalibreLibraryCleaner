using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class PersistedLibrarySnapshotsUseCaseTests
{
    [Fact]
    public async Task ListsLoadsAndSavesThroughStore()
    {
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        LibrarySnapshot snapshot = Snapshot();
        A.CallTo(() => store.ListAsync(A<CancellationToken>._))
            .Returns([new(snapshot.Identity.LibraryRoot, snapshot.ScannedAt)]);
        A.CallTo(() => store.ReadAsync(snapshot.Identity.LibraryRoot, A<CancellationToken>._))
            .Returns(snapshot);
        PersistedLibrarySnapshotsUseCase useCase = new(store);

        PersistedLibrarySnapshotListResult list = await useCase.ListAsync(CancellationToken.None);
        PersistedLibrarySnapshotLoadResult load = await useCase.LoadAsync(
            snapshot.Identity.LibraryRoot, CancellationToken.None);
        PersistedLibrarySnapshotSaveResult save = await useCase.SaveAsync(snapshot, CancellationToken.None);

        list.Snapshots.Should().ContainSingle();
        load.Snapshot.Should().BeSameAs(snapshot);
        save.IsSuccess.Should().BeTrue();
        A.CallTo(() => store.WriteAsync(snapshot, A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StoreFailureBecomesSafeOutcome()
    {
        ILibrarySnapshotStore store = A.Fake<ILibrarySnapshotStore>();
        A.CallTo(() => store.ReadAsync(A<string>._, A<CancellationToken>._))
            .ThrowsAsync(new IOException("Sensitive storage detail."));
        PersistedLibrarySnapshotsUseCase useCase = new(store);

        PersistedLibrarySnapshotLoadResult result = await useCase.LoadAsync("C:\\Books", CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotContain("Sensitive storage detail");
    }

    private static LibrarySnapshot Snapshot() => new(
        new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\Books"),
        new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
        [new(new(1), "Book", "Author", [new(new(1), "Author", "Author")], [], [], "Author/Book (1)")],
        []);
}
