using CalibreLibraryCleaner.Application.Metadata;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class OnlineMetadataSettingsViewModel(
    IGoogleBooksApiKeyStore apiKeyStore) : ObservableObject
{
    private bool _isGoogleBooksConfigured;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    public bool IsGoogleBooksConfigured
    {
        get => _isGoogleBooksConfigured;
        private set
        {
            if (!SetProperty(ref _isGoogleBooksConfigured, value)) return;
            OnPropertyChanged(nameof(GoogleBooksStatus));
            OnPropertyChanged(nameof(SaveActionLabel));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string GoogleBooksStatus => IsGoogleBooksConfigured
        ? "Google Books is enabled with a protected API key."
        : "Google Books is disabled until an API key is saved.";

    public string SaveActionLabel => IsGoogleBooksConfigured ? "_Replace key" : "_Save key";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsGoogleBooksConfigured = await apiKeyStore.IsConfiguredAsync(cancellationToken)
            .ConfigureAwait(true);
        StatusMessage = string.Empty;
    }

    public async Task SaveAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await apiKeyStore.SaveAsync(apiKey, cancellationToken).ConfigureAwait(true);
            IsGoogleBooksConfigured = true;
            StatusMessage = "Google Books API key saved with Windows current-user protection.";
        }
        catch (ArgumentException)
        {
            StatusMessage = "Enter a valid Google Books API key using letters, numbers, '-' or '_'.";
        }
        catch (GoogleBooksApiKeyStorageException)
        {
            StatusMessage = "The Google Books API key could not be saved with Windows protection.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            await apiKeyStore.ClearAsync(cancellationToken).ConfigureAwait(true);
            IsGoogleBooksConfigured = false;
            StatusMessage = "Google Books API key cleared. Open Library remains available independently.";
        }
        catch (GoogleBooksApiKeyStorageException)
        {
            StatusMessage = "The protected Google Books API key could not be cleared.";
        }
        finally
        {
            IsBusy = false;
        }
    }

}
