using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Wpf.Services;

public interface IRecoverySourceFolderPicker
{
    string? PickSourceExecutionFolder(string? initialFolder);
}

public interface IRecoveryPlanFilePicker
{
    string? PickNewRecoveryPlanPath(string? initialDirectory);
}

public interface IRecoveryWorkflowConfirmationService
{
    bool ConfirmWarningAcknowledgement(RecoveryIssue warning);
    bool ConfirmPlanApproval(RecoveryPlan plan);
    bool ConfirmRecoveryExecution(RecoveryPlan plan, string libraryRoot, string backupDestination);
}
