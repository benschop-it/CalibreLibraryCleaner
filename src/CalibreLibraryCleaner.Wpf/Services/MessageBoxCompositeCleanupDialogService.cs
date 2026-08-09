using System.Windows;
using CalibreLibraryCleaner.Application.Executions;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class MessageBoxCompositeCleanupDialogService : ICompositeCleanupDialogService
{
    public void ShowConflicts(string description) => MessageBox.Show(
        description,
        "Cleanup all conflicts",
        MessageBoxButton.OK,
        MessageBoxImage.Warning);

    public bool ConfirmExternalBackup(CompositeCleanupPlanSummary summary) => MessageBox.Show(
        $"Execute Cleanup all?\n\n"
        + $"Reviewed groups: {summary.ExactSelectionCount:N0} exact, "
        + $"{summary.MetadataSelectionCount:N0} metadata, {summary.ExpandedSelectionCount:N0} expanded.\n"
        + $"Operations: {summary.TransferCount:N0} transfers, {summary.FormatRemovalCount:N0} format removals, "
        + $"{summary.RecordRemovalCount:N0} record removals.\n\n"
        + "The application will not create or verify a backup. Continue only if a complete external backup of this Calibre library exists.",
        "Confirm Cleanup all",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;
}
