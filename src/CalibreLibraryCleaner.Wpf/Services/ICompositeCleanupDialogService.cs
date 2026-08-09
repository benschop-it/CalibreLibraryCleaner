using CalibreLibraryCleaner.Application.Executions;

namespace CalibreLibraryCleaner.Wpf.Services;

public interface ICompositeCleanupDialogService
{
    void ShowConflicts(string description);
    bool ConfirmExternalBackup(CompositeCleanupPlanSummary summary);
}
