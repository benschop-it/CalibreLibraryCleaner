using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Recovery;

internal static class InfrastructureRecoveryTestData
{
    public static RecoveryPlan Plan(bool approved = false)
    {
        CleanupPlanId sourcePlan = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        CleanupExecutionId sourceExecution =
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        RecoveryPlanId planId = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        CleanupPlanContentDigest sourceDigest = new(new string('a', 64));
        string libraryUuid = "44444444-4444-4444-4444-444444444444";
        Sha256Digest full = new(new string('1', 64));
        Sha256Digest affected = new(new string('2', 64));
        Sha256Digest unrelated = new(new string('3', 64));
        CurrentStateReconciliation reconciliation = CurrentStateReconciliation.Create(
            sourceExecution.ToString(), libraryUuid, 27, full, affected, unrelated,
            [], [], []);
        RecoveryInputIdentity input = new(sourcePlan, CleanupPlanSchemaVersion.V1,
            new(1), sourceDigest, sourceExecution, "cleanup-execution-journal/1.0",
            new(new string('4', 64)), new string('5', 64), null,
            VerifiedBackupManifest.Version, new(new string('6', 64)),
            new(new string('7', 64)), "1.0.0",
            new("C:\\calibredb.exe", "9.11.0", new(new string('8', 64)),
                "calibredb/windows/9.11.0"),
            libraryUuid, 27, new(new string('9', 64)), full, affected, unrelated,
            reconciliation.Version, reconciliation.Digest,
            "calibredb/windows/9.11.0/recovery/1.0");
        RecoveryPlanDefinition definition = new(input,
            new(sourcePlan, sourceExecution, DateTimeOffset.UnixEpoch,
                CleanupExecutionDisposition.Completed.ToString(), "C:\\backup\\execution"),
            new(sourcePlan, sourceDigest, sourceExecution, input.SourceJournalFileDigest,
                input.SourceJournalFinalEntryHash, input.OriginalManifestFileDigest,
                input.OriginalManifestInternalDigest, planId, null),
            reconciliation, new([]), new([], [], [], [], unrelated), []);
        RecoveryPlan valid = RecoveryPlanLifecyclePolicy.Create(planId, definition,
            new([], DateTimeOffset.UnixEpoch, input), DateTimeOffset.UnixEpoch);
        return approved
            ? RecoveryPlanLifecyclePolicy.Approve(valid, [], DateTimeOffset.UnixEpoch.AddMinutes(1))
            : valid;
    }
}
