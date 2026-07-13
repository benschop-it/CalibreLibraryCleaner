using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task PickerCancellationLeavesSelectionUnchanged()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns(null);
        MainWindowViewModel viewModel = CreateViewModel(picker, out _, out _);

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);

        viewModel.SelectedLibraryPath.Should().BeEmpty();
    }

    [Fact]
    public async Task SuccessfulScanDisplaysBooksAndMissingFormats()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        MainWindowViewModel viewModel = CreateViewModel(picker, out ILibraryPathResolver resolver, out ICalibreMetadataReader reader);
        ValidatedLibraryLocation location = new("library", "database");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateCatalog()));
        A.CallTo(() => resolver.ResolveFormat(
                location,
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull()))
            .Returns(ResolvedFormatPathOutcome.Success(new("full", "Book/Book.epub")));
        A.CallTo(() => resolver.FileExistsAsync(A<ResolvedFormatPath>._, A<CancellationToken>._))
            .ReturnsLazily(_ => ValueTask.FromResult(false));

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.Books.Should().ContainSingle();
        viewModel.SelectedFormats.Should().ContainSingle(format => format.Status == "Missing");
        viewModel.StatusMessage.Should().Contain("1 missing format files");
    }

    [Fact]
    public async Task CancellationShowsNeutralStateAndAllowsRetry()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        MainWindowViewModel viewModel = CreateViewModel(picker, out ILibraryPathResolver resolver, out ICalibreMetadataReader reader);
        ValidatedLibraryLocation location = new("library", "database");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .ReturnsLazily(call => WaitForCancellation(call.GetArgument<CancellationToken>(2)));
        await viewModel.SelectLibraryCommand.ExecuteAsync(null);

        Task scan = viewModel.ScanCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsBusy);
        viewModel.CancelCommand.Execute(null);
        await scan;

        viewModel.StatusMessage.Should().Contain("canceled");
        viewModel.ErrorMessage.Should().BeEmpty();
        viewModel.IsBusy.Should().BeFalse();
        viewModel.ScanCommand.CanExecute(null).Should().BeTrue();
    }

    private static MainWindowViewModel CreateViewModel(
        ILibraryFolderPicker picker,
        out ILibraryPathResolver resolver,
        out ICalibreMetadataReader reader)
    {
        resolver = A.Fake<ILibraryPathResolver>();
        reader = A.Fake<ICalibreMetadataReader>();
        IClock clock = A.Fake<IClock>();
        return new(
            new ValidateLibraryUseCase(resolver),
            new ScanLibraryUseCase(resolver, reader, clock),
            picker);
    }

    private static CalibreCatalogRecord CreateCatalog() => new(
        "87f7ed1f-59a8-45a6-975a-7e06fd84780d",
        26,
        [
            new CalibreBookRecord(
                1,
                "Book",
                "Author",
                "Book",
                [new CalibreAuthorRecord(1, "Author", "Author")],
                [],
                [new CalibreFormatRecord("EPUB", "Book")]),
        ]);

    private static async Task<CalibreCatalogReadOutcome> WaitForCancellation(
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Cancellation was expected.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
