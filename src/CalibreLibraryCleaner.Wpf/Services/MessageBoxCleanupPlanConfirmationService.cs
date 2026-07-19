using System.Windows;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class MessageBoxCleanupPlanConfirmationService : ICleanupPlanConfirmationService
{
    public bool ConfirmApproval(CleanupPlan plan) => MessageBox.Show(
        $"Approve cleanup plan {plan.Id} revision {plan.ArtifactRevision.Value}?\n\nApproval binds only to digest {plan.ContentDigest}. No Calibre change or backup will occur.",
        "Approve descriptive cleanup plan",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmRevocation(CleanupPlan plan, string reason) => MessageBox.Show(
        $"Revoke approval for cleanup plan {plan.Id}?\n\nReason: {reason}",
        "Revoke cleanup-plan approval",
        MessageBoxButton.YesNo,
        MessageBoxImage.Question,
        MessageBoxResult.No) == MessageBoxResult.Yes;
}
