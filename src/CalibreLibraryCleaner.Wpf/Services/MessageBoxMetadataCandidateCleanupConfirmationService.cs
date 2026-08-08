using System.Windows;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class MessageBoxMetadataCandidateCleanupConfirmationService
    : IMetadataCandidateCleanupConfirmationService
{
    public bool ConfirmExternalBackup(int eligibleGroupCount) => MessageBox.Show(
        $"Process {eligibleGroupCount:N0} metadata candidate group(s)?\n\n"
        + "The application will not create or verify a backup. Continue only if a complete external backup of this Calibre library exists.\n\n"
        + "Every Remove record will be deleted after complementary formats are transferred. When the keeper already has the same format, the keeper's file wins even when the removed file is not byte-identical.",
        "Confirm metadata candidate cleanup",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;
}
