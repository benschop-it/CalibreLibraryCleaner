using System.Windows;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class MessageBoxExactBinaryCleanupPlanConfirmationService :
    IExactBinaryCleanupPlanConfirmationService,
    IExactBinaryRecordDeletionConfirmation
{
    public bool ConfirmApproval(ExactBinaryCleanupPlan plan) => MessageBox.Show(
        $"Approve removing {plan.Definition.FormatRemovals.Count} byte-identical format copy or copies?\n\n" +
        $"Retain {plan.Definition.RetainedFormat.Format} on record {plan.Definition.RetainedFormat.RecordId.Value}.\n" +
        $"Records removed if empty: {RecordList(plan)}.\n" +
        $"Approval binds only to digest {plan.ContentDigest}. No Calibre change or backup will occur yet.",
        "Approve exact duplicate format cleanup",
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
            $"Remove {plan.Definition.FormatRemovals.Count} byte-identical format copy or copies now?\n\n" +
            $"Retained copy: record {plan.Definition.RetainedFormat.RecordId.Value}, {plan.Definition.RetainedFormat.Format}\n" +
            $"Records removed after becoming empty: {RecordList(plan)}\n" +
            $"Verified backup: {manifest.ManifestDigest.Value}\n\n" +
            "Calibre will remove duplicate formats and then remove only empty records.",
            "Remove exact duplicate formats",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        return Task.FromResult(confirmed);
    }

    private static string RecordList(ExactBinaryCleanupPlan plan) =>
        plan.Definition.RecordIdsToRemove.Count == 0
            ? "none"
            : string.Join(", ", plan.Definition.RecordIdsToRemove.Select(value => value.Value));
}
