using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Wpf.Services;

public interface ICleanupPlanConfirmationService
{
    bool ConfirmApproval(CleanupPlan plan);
    bool ConfirmRevocation(CleanupPlan plan, string reason);
}
