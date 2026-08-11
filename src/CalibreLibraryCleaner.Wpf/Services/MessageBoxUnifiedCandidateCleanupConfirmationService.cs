using System.Windows;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class MessageBoxUnifiedCandidateCleanupConfirmationService :
    IUnifiedCandidateCleanupConfirmationService
{
    public bool ConfirmExternalBackup(int selectedGroupCount) => MessageBox.Show(
        $"Process {selectedGroupCount:N0} unified candidate group(s)?\n\n"
        + "The application will not create or verify a backup. Continue only if a complete external backup of this Calibre library exists.\n\n"
        + "Every non-keeper record will be deleted after complementary formats are transferred. Candidate evidence can be uncertain and may not prove identical work, edition, revision, illustrations, or formatting. The selected keeper's same-format file wins.",
        "Confirm Candidate cleanup",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;
}
