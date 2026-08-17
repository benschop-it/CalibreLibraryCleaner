namespace CalibreLibraryCleaner.Wpf.Services;

public interface IOnlineMetadataSettingsDialogService
{
    void Show();
}

public sealed class OnlineMetadataSettingsDialogService(
    Func<OnlineMetadataSettingsWindow> windowFactory) : IOnlineMetadataSettingsDialogService
{
    public void Show()
    {
        OnlineMetadataSettingsWindow window = windowFactory();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();
    }
}
