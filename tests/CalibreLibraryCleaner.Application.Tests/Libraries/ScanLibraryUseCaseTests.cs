using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class ScanLibraryUseCaseTests
{
    [Fact]
    public async Task MissingFormatProducesFindingAndSuccessfulSnapshot()
    {
        ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
        ICalibreMetadataReader reader = A.Fake<ICalibreMetadataReader>();
        IClock clock = A.Fake<IClock>();
        ValidatedLibraryLocation location = new("C:\\Library", "C:\\Library\\metadata.db");
        A.CallTo(() => resolver.ValidateAsync("C:\\Library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateCatalog()));
        ResolvedFormatPath resolvedPath = new(
            "C:\\Library\\Author\\Book (1)\\Book.epub",
            "Author\\Book (1)\\Book.epub");
        A.CallTo(() => resolver.ResolveFormat(location, "Author/Book (1)", "Book", "EPUB"))
            .Returns(ResolvedFormatPathOutcome.Success(resolvedPath));
        A.CallTo(() => resolver.FileExistsAsync(resolvedPath, A<CancellationToken>._))
            .ReturnsLazily(_ => ValueTask.FromResult(false));
        DateTimeOffset scannedAt = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        A.CallTo(() => clock.GetUtcNow()).Returns(scannedAt);
        ScanLibraryUseCase useCase = new(resolver, reader, clock);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(
            "C:\\Library",
            null,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Snapshot!.ScannedAt.Should().Be(scannedAt);
        outcome.Snapshot.Books.Should().ContainSingle();
        outcome.Snapshot.Books[0].Formats[0].FileStatus.Should().Be(FormatFileStatus.Missing);
        outcome.Snapshot.Findings.Should().ContainSingle(finding => finding.Code == "FORMAT_FILE_MISSING");
    }

    [Fact]
    public async Task InvalidPathIsNotCheckedForExistence()
    {
        ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
        ICalibreMetadataReader reader = A.Fake<ICalibreMetadataReader>();
        IClock clock = A.Fake<IClock>();
        ValidatedLibraryLocation location = new("root", "database");
        A.CallTo(() => resolver.ValidateAsync("root", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateCatalog()));
        A.CallTo(() => resolver.ResolveFormat(location, A<string>._, A<string>._, A<string>._))
            .Returns(ResolvedFormatPathOutcome.Failure("Path traversal."));
        ScanLibraryUseCase useCase = new(resolver, reader, clock);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync("root", null, CancellationToken.None);

        outcome.Snapshot!.Findings.Should().ContainSingle(finding => finding.Code == "MANAGED_PATH_INVALID");
        A.CallTo(() => resolver.FileExistsAsync(A<ResolvedFormatPath>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task CancellationDuringValidationPropagates()
    {
        ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
        ICalibreMetadataReader reader = A.Fake<ICalibreMetadataReader>();
        IClock clock = A.Fake<IClock>();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        ScanLibraryUseCase useCase = new(resolver, reader, clock);

        Func<Task> act = async () => await useCase.ExecuteAsync("root", null, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        A.CallTo(() => reader.ReadAsync(
                A<ValidatedLibraryLocation>._,
                A<IProgress<LibraryScanProgress>?>._,
                A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    private static CalibreCatalogRecord CreateCatalog() => new(
        "87f7ed1f-59a8-45a6-975a-7e06fd84780d",
        26,
        [
            new CalibreBookRecord(
                1,
                "Book",
                "Author",
                "Author/Book (1)",
                [new CalibreAuthorRecord(1, "Author", "Author")],
                [new CalibreIdentifierRecord("isbn", "123")],
                [new CalibreFormatRecord("epub", "Book")]),
        ]);
}
