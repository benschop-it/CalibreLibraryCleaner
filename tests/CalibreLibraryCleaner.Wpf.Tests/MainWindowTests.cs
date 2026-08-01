using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
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
                CleanupExecutionWorkspaceViewModel cleanupExecutions = new(
                    A.Fake<IPrepareCleanupExecution>(),
                    A.Fake<IExecuteApprovedCleanupPlan>(),
                    A.Fake<IExecutionHistoryStore>(),
                    A.Fake<IExecutionBackupFolderPicker>(),
                    A.Fake<ICleanupExecutionConfirmationService>(),
                    A.Fake<IClock>());
                MainWindowViewModel viewModel = new(
                    new ValidateLibraryUseCase(resolver),
                    new ScanLibraryUseCase(
                        resolver,
                        A.Fake<ICalibreMetadataReader>(),
                        A.Fake<IFormatFileHasher>(),
                        A.Fake<IClock>(),
                        new()),
                    A.Fake<ILibraryFolderPicker>(),
                    cleanupExecutions: cleanupExecutions);
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
