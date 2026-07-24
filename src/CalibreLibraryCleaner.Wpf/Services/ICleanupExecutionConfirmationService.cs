using CalibreLibraryCleaner.Application.Executions;

namespace CalibreLibraryCleaner.Wpf.Services;

public interface ICleanupExecutionConfirmationService
{
    bool ConfirmExecution(CleanupExecutionPreparation preparation);
}
