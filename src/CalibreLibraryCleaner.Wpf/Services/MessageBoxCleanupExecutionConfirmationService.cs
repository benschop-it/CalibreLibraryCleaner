using System.Windows;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class MessageBoxCleanupExecutionConfirmationService :
    ICleanupExecutionConfirmationService,
    IDestructiveExecutionConfirmation
{
    public bool ConfirmExecution(CleanupExecutionPreparation preparation)
    {
        int additions = preparation.OperationGraph?.ConstructiveOperations.Count ?? 0;
        int removals = preparation.OperationGraph?.DestructiveOperations.Count ?? 0;
        return MessageBox.Show(
            $"Execute cleanup plan {preparation.Plan.Id} revision {preparation.Plan.ArtifactRevision.Value}?\n\n" +
            $"Library: {preparation.Plan.InputIdentity.LibraryUuid}\n" +
            $"Target record: {preparation.Plan.Definition.TargetRecordId.Value}\n" +
            $"Constructive operations: {additions}\nRecords proposed for removal: {removals}\n" +
            $"Calibre: {preparation.Tool?.Identity.ProductVersion}\n" +
            $"Plan SHA-256: {preparation.Plan.ContentDigest}\n\n" +
            "Complete verified backups will be created first. Milestone 7 has no automatic rollback.",
            "Confirm safe cleanup execution",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public async Task<bool> ConfirmAsync(
        DestructiveExecutionConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool Confirm() => MessageBox.Show(
            $"Final destructive gate for execution {request.ExecutionId}.\n\n" +
            $"Target record {request.TargetRecordId.Value} has been constructively verified.\n" +
            $"Records to remove: {string.Join(", ", request.RecordsToRemove.Select(value => value.Value))}\n" +
            $"Verified backup manifest: {request.BackupManifestDigest}\n\n" +
            "Remove these redundant records through Calibre now? Declining stops safely; any already applied constructive change remains and is reported for recovery.",
            "Final destructive confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher || dispatcher.CheckAccess())
            return Confirm();
        return await dispatcher.InvokeAsync(Confirm).Task.ConfigureAwait(false);
    }
}
