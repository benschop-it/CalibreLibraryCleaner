namespace CalibreLibraryCleaner.Wpf.Services;

public interface IExpandedCandidateCleanupConfirmationService
{
    bool ConfirmExternalBackup(int eligibleGroupCount);
}
