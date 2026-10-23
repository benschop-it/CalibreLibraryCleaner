using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CalibreLibraryCleaner.Wpf.ViewModels;

namespace CalibreLibraryCleaner.Wpf;

public partial class MainWindow : System.Windows.Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void MetadataReviewSubjectsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && DataContext is MainWindowViewModel viewModel)
            viewModel.UpdateMetadataReviewSelection(
                grid.SelectedItems.OfType<MetadataReviewSubjectRowViewModel>());
    }

    private void FinalAuthorReviewSubjectsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && DataContext is MainWindowViewModel viewModel)
            viewModel.UpdateFinalAuthorReviewSelection(
                grid.SelectedItems.OfType<FinalAuthorReviewSubjectRowViewModel>());
    }

    private void QuarantineReviewSubjectsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && DataContext is MainWindowViewModel viewModel)
            viewModel.UpdateQuarantineReviewSelection(
                grid.SelectedItems.OfType<QuarantineReviewSubjectRowViewModel>());
    }

    private void BookRowsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || sender is not DataGrid grid
            || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(grid, source) is not DataGridRow row
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        for (DependencyObject? current = source; current is not null && current != row;
             current = current is FrameworkContentElement content ? content.Parent : VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or TextBoxBase or ComboBox) return;
        }

        ICommand command = viewModel.OpenBookRowCommand;
        if (!command.CanExecute(row.Item)) return;
        command.Execute(row.Item);
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && (viewModel.IsBusy
                || viewModel.IsOperationActive
                || viewModel.ExactBinaryCleanupPlans?.IsBusy == true))
        {
            e.Cancel = true;
            MessageBox.Show(
                "A library operation is still running. Wait for its terminal result before closing the application.",
                "Library operation still running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        base.OnClosing(e);
    }
}
