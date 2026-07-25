using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Recoveries;

public sealed class RecoveryEligibilityValidator : IRecoveryEligibilityValidator
{
    public RecoveryEligibilityResult Evaluate(
        EvaluateRecoveryEligibilityRequest request,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<RecoveryIssue> issues = [.. request.Source.Issues];
        if (!request.Source.IsVerified || request.Source.CleanupPlan is null
            || request.Source.OriginalBackupManifest is null || request.Source.Journal is null)
            issues.Add(Block("RECOVERY.SOURCE_ARTIFACTS_INCOMPLETE",
                "Source execution", "The cleanup plan, execution journal, or verified original manifest is missing."));
        else
        {
            if (request.Source.CleanupPlan.SchemaVersion != CleanupPlanSchemaVersion.V1
                || request.Source.Journal.SchemaVersion != "cleanup-execution-journal/1.0"
                || request.Source.OriginalBackupManifest.PlanId != request.Source.CleanupPlan.Id
                || request.Source.OriginalBackupManifest.PlanContentDigest != request.Source.CleanupPlan.ContentDigest
                || request.Source.Journal.PlanId != request.Source.CleanupPlan.Id
                || request.Source.Journal.PlanContentDigest != request.Source.CleanupPlan.ContentDigest
                || request.Source.Journal.ExecutionId != request.Source.OriginalBackupManifest.ExecutionId)
                issues.Add(Block("RECOVERY.SOURCE_IDENTITY_MISMATCH",
                    "Source execution", "The source artifacts refer to different plans or executions."));
            if (!IsSupportedSourceApplicationVersion(request.Source.SourceApplicationVersion)
                || !IsSupportedSourceApplicationVersion(request.Source.Journal.ApplicationVersion))
                issues.Add(Block("RECOVERY.SOURCE_APPLICATION_UNSUPPORTED",
                    "Source execution",
                    "The source cleanup application version is outside the closed recovery compatibility table."));
            if (!request.Source.Journal.OriginalBackupVerifiedBeforeMutation)
                issues.Add(Block("RECOVERY.ORIGINAL_BACKUP_NOT_VERIFIED_BEFORE_MUTATION",
                    "Original backup", "The source journal does not prove backup verification before the cleanup mutation marker."));
            if (!request.Source.Journal.HasKnownDurableState
                || request.Source.Journal.Operations.Any(value =>
                    value.State == DurableSourceOperationState.Contradictory))
                issues.Add(Block("RECOVERY.JOURNAL_STATE_UNKNOWN",
                    "Execution journal", "The execution journal contains an unknown or contradictory durable operation state."));
            if (request.Source.Journal.LastState == CleanupExecutionState.Completed
                && request.Source.TerminalSummary is null)
                issues.Add(Block("RECOVERY.TERMINAL_SUMMARY_MISSING",
                    "Execution journal", "A completed source journal requires its matching immutable terminal summary."));
        }

        if (!request.CurrentState.CurrentStateIsComplete())
            issues.Add(Block("RECOVERY.CURRENT_SCAN_INCOMPLETE",
                "Current library", "A complete fresh read-only current-state scan is required."));
        if (request.Source.CleanupPlan is { } plan
            && (!string.Equals(plan.InputIdentity.LibraryUuid,
                    request.CurrentState.Snapshot.Identity.CalibreLibraryUuid, StringComparison.Ordinal)
                || plan.InputIdentity.SchemaVersion != request.CurrentState.Snapshot.Identity.SchemaVersion))
            issues.Add(Block("RECOVERY.CURRENT_LIBRARY_MISMATCH",
                "Current library", "The selected library is not the affected source library."));
        if (request.ConflictingLeaseOrRecovery)
            issues.Add(Block("RECOVERY.CONFLICTING_MUTATION_OR_RECOVERY",
                "Library lease", "Another cleanup or recovery execution is active or unresolved."));
        if (!request.CapabilityProfile.Supports(RecoveryCapability.ExportCurrentRecord))
            issues.Add(Block("RECOVERY.CURRENT_BACKUP_EXPORT_UNSUPPORTED",
                "Recovery capability", "The exact Calibre profile cannot create the mandatory current-state record exports."));
        if (!request.CapabilityProfile.Supports(RecoveryCapability.VerifyRestoredContent))
            issues.Add(Block("RECOVERY.SEMANTIC_VERIFICATION_UNSUPPORTED",
                "Recovery capability", "The exact profile does not enable semantic recovery verification."));
        if (!string.Equals(request.CapabilityProfile.ToolIdentity.ProductVersion,
                request.Source.SourceToolIdentity?.ProductVersion, StringComparison.Ordinal))
            issues.Add(Block("RECOVERY.CALIBRE_VERSION_MISMATCH",
                "Recovery capability", "The source and current exact Calibre versions are not in a tested recovery compatibility entry."));
        if (string.IsNullOrWhiteSpace(request.CanonicalRootIdentity))
            issues.Add(Block("RECOVERY.ROOT_IDENTITY_REQUIRED",
                "Current library", "The canonical current library root identity is required."));

        return new(issues, evaluatedAtUtc);
    }

    internal static bool IsSupportedSourceApplicationVersion(string? value) =>
        value is "1.0.0" or "1.0.0.0";

    private static RecoveryIssue Block(string code, string subject, string explanation) =>
        new(code, RecoveryIssueSeverity.Blocking, subject, explanation);
}

internal static class RecoveryCurrentStateSnapshotExtensions
{
    public static bool CurrentStateIsComplete(this RecoveryCurrentStateSnapshot value) =>
        value.Snapshot.Findings.All(finding => !string.Equals(finding.Code, "SCAN_FAILED", StringComparison.OrdinalIgnoreCase))
        && !string.IsNullOrWhiteSpace(value.FullFingerprint.Value)
        && !string.IsNullOrWhiteSpace(value.AffectedFingerprint.Value)
        && !string.IsNullOrWhiteSpace(value.UnrelatedFingerprint.Value);
}
