using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public void WindowCanBeShownWithReadOnlyViewModelBindings()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
                MainWindowViewModel viewModel = new(
                    new ValidateLibraryUseCase(resolver),
                    new ScanLibraryUseCase(
                        resolver,
                        A.Fake<ICalibreMetadataReader>(),
                        A.Fake<IFormatFileHasher>(),
                        A.Fake<IClock>(),
                        new()),
                    A.Fake<ILibraryFolderPicker>());
                MainWindow window = new(viewModel);
                window.Show();
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);

        thread.Start();
        bool completed = thread.Join(TimeSpan.FromSeconds(10));

        completed.Should().BeTrue();
        failure.Should().BeNull();
    }
}
