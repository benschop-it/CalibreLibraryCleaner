using Microsoft.Win32;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class OpenFolderDialogLibraryFolderPicker : ILibraryFolderPicker
{
    public string? PickFolder(string? initialFolder)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select a Calibre library folder",
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialFolder))
        {
            dialog.InitialDirectory = initialFolder;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
