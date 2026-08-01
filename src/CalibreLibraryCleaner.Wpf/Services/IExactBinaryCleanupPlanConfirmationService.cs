using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Wpf.Services;

public interface IExactBinaryCleanupPlanConfirmationService
{
    bool ConfirmApproval(ExactBinaryCleanupPlan plan);
}
