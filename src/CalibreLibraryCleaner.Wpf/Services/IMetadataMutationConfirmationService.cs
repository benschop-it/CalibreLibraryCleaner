namespace CalibreLibraryCleaner.Wpf.Services;

public interface IMetadataMutationConfirmationService
{
    bool ConfirmExternalBackup(int selectedProposalCount);
}
