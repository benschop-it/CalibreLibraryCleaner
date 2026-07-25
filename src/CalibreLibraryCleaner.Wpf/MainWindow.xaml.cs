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
            && (viewModel.CleanupExecutions?.IsBusy == true
                || viewModel.Recoveries?.IsBusy == true))
        {
            e.Cancel = true;
            MessageBox.Show(
                "A cleanup or recovery workflow is still running. The application cannot close safely until it reaches and persists a terminal boundary. Use the safe stop action and wait for the terminal result.",
                "Library workflow still running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        base.OnClosing(e);
    }
}
