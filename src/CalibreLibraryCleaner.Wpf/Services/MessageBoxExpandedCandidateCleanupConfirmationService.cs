using System.Windows;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class MessageBoxExpandedCandidateCleanupConfirmationService :
    IExpandedCandidateCleanupConfirmationService
{
    public bool ConfirmExternalBackup(int selectedGroupCount) => MessageBox.Show(
        $"Process {selectedGroupCount:N0} expanded candidate group(s)?\n\n"
        + "The application will not create or verify a backup. Continue only if a complete external backup of this Calibre library exists.\n\n"
        + "Every Remove record will be deleted after complementary formats are transferred. Candidate matching can be uncertain and may not prove identical work, edition, revision, illustrations, or formatting. The selected keeper's same-format file wins.",
        "Confirm expanded candidate cleanup",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;
}
