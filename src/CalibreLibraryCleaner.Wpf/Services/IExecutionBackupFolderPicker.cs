namespace CalibreLibraryCleaner.Wpf.Services;

public interface IExecutionBackupFolderPicker
{
    string? PickBackupFolder(string? initialFolder);
}
