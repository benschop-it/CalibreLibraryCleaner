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
            && viewModel.ExactBinaryCleanupPlans?.IsBusy == true)
        {
            e.Cancel = true;
            MessageBox.Show(
                "Exact duplicate cleanup is still running. Wait for the current worker chunk and terminal result before closing the application.",
                "Duplicate cleanup still running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        base.OnClosing(e);
    }
}
