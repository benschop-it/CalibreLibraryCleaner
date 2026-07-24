using Microsoft.Win32;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class OpenFolderDialogExecutionBackupFolderPicker : IExecutionBackupFolderPicker
{
    public string? PickBackupFolder(string? initialFolder)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select an external cleanup execution backup folder",
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialFolder)) dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
