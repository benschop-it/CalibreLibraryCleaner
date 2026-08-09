using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CalibreLibraryCleaner.Wpf.ViewModels;

namespace CalibreLibraryCleaner.Wpf;

public partial class MainWindow : System.Windows.Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void ExactDuplicateMembersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        RouteRowDoubleClick(sender, e, viewModel => viewModel.OpenSelectedExactDuplicateCommand);

    private void MetadataCandidateMembersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        RouteRowDoubleClick(sender, e, viewModel => viewModel.OpenSelectedMetadataCandidateCommand);

    private void ExpandedCandidateMembersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        RouteRowDoubleClick(sender, e, viewModel => viewModel.OpenSelectedExpandedCandidateCommand);

    private void RouteRowDoubleClick(
        object sender,
        MouseButtonEventArgs e,
        Func<MainWindowViewModel, ICommand> commandSelector)
    {
        if (e.ChangedButton != MouseButton.Left
            || sender is not DataGrid grid
            || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(grid, source) is not DataGridRow
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        ICommand command = commandSelector(viewModel);
        if (!command.CanExecute(null)) return;
        command.Execute(null);
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && (viewModel.ExactBinaryCleanupPlans?.IsBusy == true
                || viewModel.MetadataCandidateCleanup?.IsBusy == true
                || viewModel.ExpandedCandidateCleanup?.IsBusy == true
                || viewModel.CompositeCleanup?.IsBusy == true))
        {
            e.Cancel = true;
            MessageBox.Show(
                "Duplicate cleanup is still running. Wait for the current worker chunk and terminal result before closing the application.",
                "Cleanup still running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        base.OnClosing(e);
    }
}
