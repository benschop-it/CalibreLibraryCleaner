using System.Windows;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class MessageBoxExactDuplicateCleanupConfirmationService
    : IExactDuplicateCleanupConfirmationService
{
    public bool ConfirmExternalBackup(int eligibleGroupCount) => MessageBox.Show(
        $"Remove exact duplicates from {eligibleGroupCount:N0} group(s)?\n\n"
        + "The application will not create or verify a backup. Continue only if a complete external backup of this Calibre library exists.",
        "Confirm external backup",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;
}
