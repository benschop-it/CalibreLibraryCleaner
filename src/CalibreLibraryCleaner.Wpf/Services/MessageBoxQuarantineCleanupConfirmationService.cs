using System.Windows;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class MessageBoxQuarantineCleanupConfirmationService :
    IQuarantineCleanupConfirmationService
{
    public bool ConfirmMove(int selectedRecordCount, string destinationRoot, bool destinationExists) =>
        MessageBox.Show(
            $"Move {selectedRecordCount:N0} reviewed record(s) to:\n{destinationRoot}\n\n"
            + (destinationExists
                ? "The existing destination library will be reused. Existing records will not be merged or overwritten.\n\n"
                : "A new Calibre library will be created at this location.\n\n")
            + "The application will not create or verify backups. Continue only if a complete external backup "
            + "of the source and any existing destination library exists.",
            "Confirm unresolvable record quarantine",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}
