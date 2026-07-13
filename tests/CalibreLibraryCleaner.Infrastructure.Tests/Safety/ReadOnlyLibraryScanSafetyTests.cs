using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Safety;

public sealed class ReadOnlyLibraryScanSafetyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScanDoesNotChangeAnythingInsideSyntheticLibrary(bool createExpectedFormat)
    {
        using SyntheticCalibreLibrary library = new();
        library.AddBook(createExpectedFormat);
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(
            library.RootPath,
            null,
            CancellationToken.None);
        IReadOnlyList<LibraryEntryState> after = LibraryStateCapture.Capture(library.RootPath);

        outcome.IsSuccess.Should().BeTrue();
        after.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
        Directory.EnumerateFiles(library.RootPath, "metadata.db-*", SearchOption.TopDirectoryOnly)
            .Should().BeEmpty();
        if (!createExpectedFormat)
        {
            outcome.Snapshot!.Findings.Should().ContainSingle(finding => finding.Code == "FORMAT_FILE_MISSING");
        }
    }

    [Fact]
    public async Task MissingExpectedFileDoesNotRenameAlternateFormatFile()
    {
        using SyntheticCalibreLibrary library = new();
        string expected = library.AddBook(createFormatFile: false);
        string directory = Path.GetDirectoryName(expected)!;
        Directory.CreateDirectory(directory);
        string alternate = Path.Combine(directory, "Alternate.epub");
        await File.WriteAllBytesAsync(alternate, [1, 2, 3]);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);

        LibraryScanOutcome outcome = await useCase.ExecuteAsync(
            library.RootPath,
            null,
            CancellationToken.None);

        outcome.Snapshot!.Findings.Should().ContainSingle(finding => finding.Code == "FORMAT_FILE_MISSING");
        File.Exists(expected).Should().BeFalse();
        File.ReadAllBytes(alternate).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task CanceledReadDoesNotChangeAnythingInsideSyntheticLibrary()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddBook();
        IReadOnlyList<LibraryEntryState> before = LibraryStateCapture.Capture(library.RootPath);
        using ServiceProvider provider = TestServices.CreateProvider();
        ScanLibraryUseCase useCase = TestServices.CreateScanUseCase(provider);
        using CancellationTokenSource cancellation = new();
        InlineProgress progress = new(update =>
        {
            if (update.Phase == LibraryScanPhase.ReadingBooks)
            {
                cancellation.Cancel();
            }
        });

        Func<Task> act = async () => await useCase.ExecuteAsync(
            library.RootPath,
            progress,
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        LibraryStateCapture.Capture(library.RootPath)
            .Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    private sealed class InlineProgress(Action<LibraryScanProgress> report) : IProgress<LibraryScanProgress>
    {
        public void Report(LibraryScanProgress value) => report(value);
    }
}
