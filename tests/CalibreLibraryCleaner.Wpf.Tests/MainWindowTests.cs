using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public void EveryBookGridOpensClickedRowButNotHeadersEmptySpaceOrCheckboxes()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                string[] bookSources =
                [
                    "Books", "SelectedFormats", "SelectedExactDuplicateMembers",
                    "SelectedMetadataDuplicateMembers", "SelectedUnifiedCandidateMembers",
                    "MetadataReviewSubjects", "FinalAuthorReviewSubjects", "QuarantineReviewSubjects",
                    "SelectedExpandedCandidateMembers", "EpubAssessments", "PdfAssessments",
                ];
                HashSet<string> visited = [];
                List<string> opened = [];
                ILibraryPathResolver resolver = A.Fake<ILibraryPathResolver>();
                IEbookViewerLauncher viewer = A.Fake<IEbookViewerLauncher>();
                A.CallTo(() => viewer.LaunchAsync(A<EbookViewerLaunchRequest>._, A<CancellationToken>._))
                    .Invokes(call => opened.Add(call.GetArgument<EbookViewerLaunchRequest>(0)!.ExpectedRelativePath))
                    .Returns(EbookViewerLaunchResult.Success());
                using MainWindowViewModel viewModel = new(
                    new ValidateLibraryUseCase(resolver),
                    new ScanLibraryUseCase(resolver, A.Fake<ICalibreMetadataReader>(),
                        A.Fake<IFormatFileHasher>(), A.Fake<IClock>(), new()),
                    A.Fake<ILibraryFolderPicker>(), ebookViewer: viewer);
                viewModel.SelectedPersistedLibraryPath = "C:\\Books";
                MainWindow window = new(viewModel);
                try
                {
                    window.Show();
                    TabControl tabs = Descendants<TabControl>(window).Single();
                    foreach (TabItem tab in tabs.Items)
                    {
                        tabs.SelectedItem = tab;
                        window.UpdateLayout();
                        foreach (DataGrid grid in Descendants<DataGrid>(tabs).ToArray())
                        {
                            string? source = BindingOperations.GetBindingExpression(grid, ItemsControl.ItemsSourceProperty)
                                ?.ParentBinding.Path.Path;
                            if (source is null || !bookSources.Contains(source, StringComparer.Ordinal)) continue;
                            visited.Add(source).Should().BeTrue();
                            FormatRowViewModel[] rows =
                            [
                                FormatRow("Author/Selected.epub"),
                                FormatRow("Author/Clicked.epub"),
                            ];
                            grid.ItemsSource = rows;
                            grid.Columns.Clear();
                            grid.Columns.Add(new DataGridTextColumn
                            {
                                Binding = new Binding(nameof(FormatRowViewModel.ExpectedRelativePath)),
                            });
                            grid.Columns.Add(new DataGridCheckBoxColumn());
                            grid.SelectedItem = rows[0];
                            grid.ScrollIntoView(rows[1]);
                            window.UpdateLayout();
                            DataGridRow row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(1);
                            row.Should().NotBeNull(source);
                            int before = opened.Count;

                            RaiseDoubleClick(grid, grid);
                            RaiseDoubleClick(grid, Descendants<DataGridColumnHeader>(grid).First());
                            RaiseDoubleClick(grid, Descendants<CheckBox>(row).Single());
                            opened.Should().HaveCount(before, source);

                            RaiseDoubleClick(grid, row);

                            opened.Should().HaveCount(before + 1, source);
                            opened[^1].Should().Be("Author/Clicked.epub", source);
                            grid.SelectedItem.Should().BeSameAs(rows[0], source);
                        }
                    }
                    visited.Should().BeEquivalentTo(bookSources);
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue();
        failure.Should().BeNull();
    }

    private static FormatRowViewModel FormatRow(string relativePath) => new(new(
        "EPUB", "Book", relativePath, FormatFileStatus.Present,
        new(100, new(new string('a', 64))), new(100, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0)));

    private static void RaiseDoubleClick(DataGrid grid, DependencyObject source) => grid.RaiseEvent(new MouseButtonEventArgs(
        Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
    {
        RoutedEvent = Control.MouseDoubleClickEvent,
        Source = source,
    });

    private static IEnumerable<TElement> Descendants<TElement>(DependencyObject parent) where TElement : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is TElement element) yield return element;
            foreach (TElement descendant in Descendants<TElement>(child)) yield return descendant;
        }
    }

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
                TabControl tabs = Descendants<TabControl>(window).Single();
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(item =>
                    string.Equals(item.Header?.ToString(), "_Unresolvable", StringComparison.Ordinal));
                window.UpdateLayout();
                DataGridCheckBoxColumn manualColumn = Descendants<DataGrid>(window)
                    .SelectMany(grid => grid.Columns)
                    .OfType<DataGridCheckBoxColumn>()
                    .Single(column => string.Equals(column.Header?.ToString(), "Manual", StringComparison.Ordinal));
                Binding manualBinding = manualColumn.Binding.Should().BeOfType<Binding>().Subject;
                manualBinding.Path.Path.Should().Be(nameof(QuarantineReviewSubjectRowViewModel.IsManual));
                manualBinding.Mode.Should().Be(BindingMode.OneWay);
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
