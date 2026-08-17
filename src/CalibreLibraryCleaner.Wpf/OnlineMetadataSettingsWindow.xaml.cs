using System.ComponentModel;
using System.Windows;
using CalibreLibraryCleaner.Wpf.ViewModels;

namespace CalibreLibraryCleaner.Wpf;

public partial class OnlineMetadataSettingsWindow : Window
{
    private readonly OnlineMetadataSettingsViewModel _viewModel;

    public OnlineMetadataSettingsWindow(OnlineMetadataSettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) =>
        await _viewModel.InitializeAsync().ConfigureAwait(true);

    private async void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        string apiKey = GoogleBooksApiKeyBox.Password;
        GoogleBooksApiKeyBox.Clear();
        await _viewModel.SaveAsync(apiKey).ConfigureAwait(true);
    }

    private async void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        GoogleBooksApiKeyBox.Clear();
        await _viewModel.ClearAsync().ConfigureAwait(true);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
