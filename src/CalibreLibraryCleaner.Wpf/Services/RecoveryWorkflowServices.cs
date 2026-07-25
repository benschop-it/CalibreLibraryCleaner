using System.Windows;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Recoveries;
using Microsoft.Win32;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class OpenFolderDialogRecoverySourceFolderPicker : IRecoverySourceFolderPicker
{
    public string? PickSourceExecutionFolder(string? initialFolder)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Select one Milestone 7 execution backup bundle",
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialFolder)) dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

internal sealed class SaveFileDialogRecoveryPlanFilePicker : IRecoveryPlanFilePicker
{
    public string? PickNewRecoveryPlanPath(string? initialDirectory)
    {
        SaveFileDialog dialog = new()
        {
            Title = "Export immutable recovery plan",
            Filter = "Recovery plan (*.recovery-plan.json)|*.recovery-plan.json",
            AddExtension = true,
            DefaultExt = ".recovery-plan.json",
            OverwritePrompt = true,
            FileName = $"recovery-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.recovery-plan.json",
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

internal sealed class MessageBoxRecoveryWorkflowConfirmationService :
    IRecoveryWorkflowConfirmationService,
    IDestructiveRecoveryConfirmation
{
    public bool ConfirmWarningAcknowledgement(RecoveryIssue warning)
    {
        string message =
            $"Acknowledge recovery warning {warning.Code}?" + Environment.NewLine
            + Environment.NewLine + warning.Subject + Environment.NewLine
            + warning.Explanation + Environment.NewLine + Environment.NewLine
            + "This acknowledgement will be bound individually to the immutable recovery-plan approval.";
        return MessageBox.Show(message, "Acknowledge recovery warning",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            == MessageBoxResult.Yes;
    }

    public bool ConfirmPlanApproval(RecoveryPlan plan)
    {
        string warningCodes = plan.Definition.RequiredWarningCodes.Count == 0
            ? "No acknowledgement warnings."
            : string.Join(Environment.NewLine, plan.Definition.RequiredWarningCodes);
        string message =
            $"Approve immutable recovery plan {plan.Id}?" + Environment.NewLine + Environment.NewLine
            + $"Canonical SHA-256: {plan.ContentDigest}" + Environment.NewLine
            + $"Constructive operations: {plan.Definition.OperationGraph.ConstructiveOperations.Count}" + Environment.NewLine
            + $"Destructive operations: {plan.Definition.OperationGraph.DestructiveOperations.Count}" + Environment.NewLine
            + $"Preserved unexpected items: {plan.Definition.ExpectedFinalState.PreservedContent.Count}"
            + Environment.NewLine + Environment.NewLine + warningCodes
            + Environment.NewLine + Environment.NewLine
            + "Approval is bound to the current scan, source artifacts, library identity, and exact capability profile.";
        return MessageBox.Show(message, "Approve verified recovery plan",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public bool ConfirmRecoveryExecution(
        RecoveryPlan plan,
        string libraryRoot,
        string backupDestination)
    {
        string message =
            $"Begin recovery for exactly one approved plan?" + Environment.NewLine + Environment.NewLine
            + $"Plan: {plan.Id}" + Environment.NewLine
            + $"Library: {libraryRoot}" + Environment.NewLine
            + $"New current-state backup: {backupDestination}" + Environment.NewLine
            + $"Plan digest: {plan.ContentDigest}" + Environment.NewLine + Environment.NewLine
            + "The original backup will be reverified and a new current-state backup must verify before mutation. "
            + "Cancellation stops only at a verified safe operation boundary after mutation starts.";
        return MessageBox.Show(message, "Confirm recovery preflight",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public Task<bool> ConfirmAsync(
        DestructiveRecoveryConfirmationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string operations = string.Join(Environment.NewLine,
            request.DestructiveOperations.Select(value =>
                $"- {value.Id}: {value.Kind}; logical={value.LogicalRecordId}; "
                + $"current-record={value.CurrentRecordId?.ToString() ?? "none"}; "
                + $"format={value.Format ?? "none"}; "
                + $"expected-current={value.ExpectedCurrentFingerprint?.Sha256.Value ?? "none"}; "
                + $"dependencies=[{string.Join(", ", value.DependencyIds)}]; "
                + $"reason={value.Reason}"));
        string message =
            "Constructive recovery and intermediate semantic verification passed."
            + Environment.NewLine + Environment.NewLine
            + $"Approve these destructive recovery operations last?" + Environment.NewLine
            + operations + Environment.NewLine + Environment.NewLine
            + $"Destructive graph SHA-256: {request.DestructiveOperationDigest}" + Environment.NewLine
            + $"Current-state manifest file SHA-256: "
            + request.CurrentStateBackupManifestFileDigest + Environment.NewLine
            + $"Current-state manifest internal SHA-256: "
            + request.CurrentStateBackupManifestInternalDigest;
        bool result = MessageBox.Show(message, "Confirm destructive recovery phase",
            MessageBoxButton.YesNo, MessageBoxImage.Stop, MessageBoxResult.No) == MessageBoxResult.Yes;
        return Task.FromResult(result);
    }
}
