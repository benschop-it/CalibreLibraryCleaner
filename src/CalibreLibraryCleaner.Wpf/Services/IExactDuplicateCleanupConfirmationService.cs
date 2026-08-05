namespace CalibreLibraryCleaner.Wpf.Services;

public interface IExactDuplicateCleanupConfirmationService
{
    bool ConfirmExternalBackup(int eligibleGroupCount);
}
