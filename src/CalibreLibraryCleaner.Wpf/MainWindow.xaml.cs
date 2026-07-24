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
        if (DataContext is MainWindowViewModel { CleanupExecutions.IsMutationInFlight: true })
        {
            e.Cancel = true;
            MessageBox.Show(
                "A Calibre mutation is still running. The application cannot close safely until the active command exits and fresh verification finishes. Use \"Stop safely after current operation\" and wait for a terminal result.",
                "Cleanup execution still running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        base.OnClosing(e);
    }
}
