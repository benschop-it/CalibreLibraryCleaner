namespace CalibreLibraryCleaner.Wpf.Services;

public interface IMetadataCandidateCleanupConfirmationService
{
    bool ConfirmExternalBackup(int eligibleGroupCount);
}
