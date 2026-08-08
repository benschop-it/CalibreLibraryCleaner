using System.ComponentModel;
using System.Windows;
using CalibreLibraryCleaner.Wpf.ViewModels;

namespace CalibreLibraryCleaner.Wpf;

public partial class MainWindow : System.Windows.Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && (viewModel.ExactBinaryCleanupPlans?.IsBusy == true
                || viewModel.MetadataCandidateCleanup?.IsBusy == true))
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
