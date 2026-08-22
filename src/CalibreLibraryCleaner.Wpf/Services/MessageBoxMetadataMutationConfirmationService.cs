using System.Windows;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class MessageBoxMetadataMutationConfirmationService :
    IMetadataMutationConfirmationService
{
    public bool ConfirmExternalBackup(int selectedProposalCount) => MessageBox.Show(
        $"Apply {selectedProposalCount:N0} checked online metadata proposal(s)?\n\n"
        + "The application will not create or verify a backup. Continue only if a complete external backup of this Calibre library exists.",
        "Confirm metadata update",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmAuthorNormalization(int bookCount) => MessageBox.Show(
        $"Normalize author display names and exact sort values for {bookCount:N0} book(s)?\n\n"
        + "Calibre may move managed folders after author changes. The application will not create or verify a backup. Continue only if a complete external backup exists.",
        "Confirm author normalization",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;
}
