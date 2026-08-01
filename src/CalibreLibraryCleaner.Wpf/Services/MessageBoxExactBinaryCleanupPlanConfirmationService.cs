using System.Windows;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class MessageBoxExactBinaryCleanupPlanConfirmationService :
    IExactBinaryCleanupPlanConfirmationService,
    IExactBinaryRecordDeletionConfirmation
{
    public bool ConfirmApproval(ExactBinaryCleanupPlan plan) => MessageBox.Show(
        $"Approve keeping one record and deleting the other {plan.Definition.RecordIdsToRemove.Count} duplicate record(s)?\n\n" +
        $"Keep record {plan.Definition.RetainedFormat.RecordId.Value}.\n" +
        $"Delete records: {string.Join(", ", plan.Definition.RecordIdsToRemove.Select(value => value.Value))}.\n" +
        $"Approval binds only to digest {plan.ContentDigest}. No Calibre change or backup will occur yet.",
        "Approve exact duplicate consolidation",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    public Task<bool> ConfirmAsync(
        ExactBinaryCleanupPlan plan,
        ExactBinaryRecordBackupManifest manifest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool confirmed = MessageBox.Show(
            $"Delete the following Calibre book records now?\n\n" +
            $"{string.Join(", ", plan.Definition.RecordIdsToRemove.Select(value => value.Value))}\n\n" +
            $"Keeper: {plan.Definition.RetainedFormat.RecordId.Value}\n" +
            $"Verified backup: {manifest.ManifestDigest.Value}\n\n" +
            "Calibre will perform non-permanent record removal.",
            "Consolidate exact duplicate books",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        return Task.FromResult(confirmed);
    }
}
