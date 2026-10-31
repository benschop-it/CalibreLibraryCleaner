using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Metadata;
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
                DataGrid review = Descendants<DataGrid>(window).Single(grid =>
                    BindingOperations.GetBindingExpression(grid, ItemsControl.ItemsSourceProperty)
                        ?.ParentBinding.Path.Path == nameof(MainWindowViewModel.QuarantineReviewSubjects));
                review.Columns.Select(column => column.Header?.ToString())
                    .Should().Equal("Move", "Record", "Title", "Authors", "Problem to review");
                review.Columns.OfType<DataGridCheckBoxColumn>().Should().BeEmpty();
                review.Columns.OfType<DataGridTemplateColumn>().Should().ContainSingle();
                Descendants<ComboBox>(tabs).Should().BeEmpty(
                    "Unresolvable review must not expose seven overlapping qualification filters");
                CheckBox showAll = Descendants<CheckBox>(tabs).Single();
                BindingOperations.GetBindingExpression(showAll, ToggleButton.IsCheckedProperty)!
                    .ParentBinding.Path.Path.Should().Be(nameof(MainWindowViewModel.ShowAllQuarantineRecords));
                showAll.IsChecked.Should().BeFalse();
                Descendants<Button>(tabs).Should().Contain(button =>
                    button.Command == viewModel.CheckAllQuarantineIncludeCommand
                    && button.Content.ToString() == "Check all _shown");
                Descendants<Button>(tabs).Should().Contain(button =>
                    button.Command == viewModel.ReviewIdentityRepairsCommand
                    && button.Content.ToString() == "Find _repairs for selected"
                    && !button.IsEnabled);
                bool persisted = false;
                QuarantineReviewSubjectRowViewModel example = new(
                    new(new(42), "Mara Vale - The Mountain", [new(new(1), "Mara Vale", "Vale, Mara")], [],
                        new([], [])),
                    (_, include) =>
                    {
                        persisted = include;
                        return Task.CompletedTask;
                    });
                review.ItemsSource = new[] { example };
                review.SelectedItem = example;
                viewModel.SelectedQuarantineReviewSubject = example;
                review.ScrollIntoView(example);
                window.UpdateLayout();
                DataGridRow exampleRow = (DataGridRow)review.ItemContainerGenerator.ContainerFromIndex(0);
                CheckBox move = Descendants<CheckBox>(exampleRow).Single();
                move.IsEnabled.Should().BeTrue("Move is an editable choice even though text cells are read-only");
                typeof(ToggleButton).GetMethod("OnClick",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(move, null);
                example.Include.Should().BeTrue();
                persisted.Should().BeTrue("the actual checkbox must execute its bound selection command");
                if (Environment.GetEnvironmentVariable("CLC_PROBLEM_REVIEW_CAPTURE") is { Length: > 0 } capture)
                {
                    window.UpdateLayout();
                    System.Windows.Media.Imaging.RenderTargetBitmap image = new(
                        (int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(window);
                    System.Windows.Media.Imaging.PngBitmapEncoder encoder = new();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    using System.IO.FileStream output = System.IO.File.Create(capture);
                    encoder.Save(output);
                }
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
