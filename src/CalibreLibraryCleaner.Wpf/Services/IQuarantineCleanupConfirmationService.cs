namespace CalibreLibraryCleaner.Wpf.Services;

public interface IQuarantineCleanupConfirmationService
{
    bool ConfirmMove(int selectedRecordCount, string destinationRoot, bool destinationExists);
}
