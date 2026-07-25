using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Application.Tests.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;
using FakeItEasy;

namespace CalibreLibraryCleaner.Application.Tests.Recoveries;

internal static class RecoveryTestData
{
    public static readonly CleanupExecutionId SourceExecutionId =
        new(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    public static readonly RecoveryPlanId RecoveryPlanId =
        new(Guid.Parse("99999999-8888-7777-6666-555555555555"));

    public static (
        RecoverySourceInspection Source,
        RecoveryCurrentStateSnapshot Current,
        RecoveryCapabilityProfile Profile,
        CalibreToolDescriptor Tool) SourceAndCurrent()
    {
        (CleanupPlan plan, _, _, LibrarySnapshot final) = ExecutionTestData.Approved();
        CleanupExecutionCapabilityResult graph = CleanupExecutionCapabilityPolicy.Evaluate(plan);
        SourceOperationProjection[] operations = graph.Graph!.Operations.Select(value =>
            new SourceOperationProjection(value.Id, value.Kind,
                DurableSourceOperationState.DurablyCompleted, value.TargetRecordId,
                value.SourceRecordId, value.Format, value.DependencyIds)).ToArray();
        BackupManifestEntry[] manifestEntries = plan.Definition.BackupRequirements
            .Where(value => value.Kind != BackupRequirementKind.ExecutionAudit)
            .Select((value, index) => new BackupManifestEntry(
                $"artifact/{index:D3}.bin", BackupArtifactKind.PreflightEvidence,
                1, new(new string((char)('a' + index % 6), 64)), [value.Id],
                value.RecordId, value.Format)).ToArray();
        VerifiedBackupManifest manifest = VerifiedBackupManifest.Create(
            SourceExecutionId, plan, ExecutionTestData.Now, manifestEntries);
        VerifiedExecutionJournalProjection journal = new(
            "cleanup-execution-journal/1.0", SourceExecutionId, plan.Id,
            plan.ContentDigest, plan.InputIdentity.LibraryUuid, "1.0.0",
            new(new string('d', 64)), new string('e', 64),
            new(new string('f', 64)), CleanupExecutionState.Completed,
            CleanupExecutionDisposition.Completed, true, true, operations,
            ["JournalHeader", "BackupVerified", "MutationStarting",
                "OperationVerified", "TerminalSummary"], true);
        ExecutionToolIdentity identity = new("C:\\trusted\\calibredb.exe", "9.11.0",
            new(new string('b', 64)), "calibredb/windows/9.11.0");
        CalibreToolDescriptor tool = new(identity.CanonicalExecutableIdentity, identity,
            Enum.GetValues<CalibreExecutionCapability>());
        RecoveryCapabilityProfile profile = new(
            "calibredb/windows/9.11.0/recovery/1.0", identity,
            Enum.GetValues<RecoveryCapability>().Select(value =>
                new RecoveryCapabilityStatus(value, true, true, true, true, "test-qualified")));
        List<VerifiedRecoverySourceArtifact> artifacts = [];
        foreach (ExpectedRecordState record in plan.Definition.ExpectedLibraryState.Records)
        {
            artifacts.Add(new($"exports/{record.RecordId.Value}/metadata.opf",
                $"C:\\backup\\exports\\{record.RecordId.Value}\\metadata.opf",
                RecoverySourceArtifactKind.OriginalMetadataOpf, 10,
                new(new string('c', 64)), record.RecordId));
            foreach (ExpectedFormatState format in record.Formats)
                artifacts.Add(new($"raw-formats/{record.RecordId.Value}/book.{format.Format.ToLowerInvariant()}",
                    $"C:\\backup\\raw-formats\\{record.RecordId.Value}\\book.{format.Format.ToLowerInvariant()}",
                    RecoverySourceArtifactKind.OriginalRawFormat,
                    format.Fingerprint.SizeInBytes, format.Fingerprint.Sha256,
                    record.RecordId, format.Format));
        }
        ExecutionHistoryEntry summary = new(SourceExecutionId, plan.Id, plan.ContentDigest,
            plan.InputIdentity.LibraryUuid, CleanupExecutionState.Completed,
            CleanupExecutionDisposition.Completed, CleanupExecutionFailureClassification.None,
            "C:\\backup\\execution", "C:\\backup\\execution\\execution.journal.jsonl",
            manifest.ManifestDigest.Value, ExecutionTestData.Now, true);
        RecoverySourceInspection source = new("C:\\backup\\execution", plan, manifest,
            journal, summary, identity, "1.0.0", new(new string('9', 64)),
            artifacts, []);
        CalibreBookId[] affected = plan.Definition.InvolvedRecordIds.ToArray();
        RecoveryCoverEvidence[] covers = final.Books.Where(book => affected.Contains(book.Id))
            .Select(book => new RecoveryCoverEvidence(book.Id, false, null, null)).ToArray();
        RecoveryCurrentStateSnapshot current = new(final, covers,
            RecoverySnapshotFingerprintPolicy.ComputeFull(final, covers),
            RecoverySnapshotFingerprintPolicy.ComputeAffected(final, affected, covers),
            RecoverySnapshotFingerprintPolicy.ComputeUnrelated(final, affected, covers));
        return (source, current, profile, tool);
    }

    public static RecoveryPlan ValidPlan()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current,
            RecoveryCapabilityProfile profile, _) = SourceAndCurrent();
        CurrentStateReconciliation reconciliation = new CurrentStateReconciler()
            .Reconcile(source, current);
        IRecoveryIdGenerator ids = A.Fake<IRecoveryIdGenerator>();
        A.CallTo(() => ids.CreatePlanId()).Returns(RecoveryPlanId);
        RecoveryPlanGenerator generator = new(ids, new RecoveryPlanValidator());
        return generator.Generate(new(source, reconciliation,
            current.Snapshot.Identity.LibraryRoot, profile), ExecutionTestData.Now);
    }

    public static RecoveryCurrentStateSnapshot CurrentFrom(
        RecoverySourceInspection source,
        LibrarySnapshot snapshot)
    {
        CalibreBookId[] affected = source.CleanupPlan!.Definition.InvolvedRecordIds.ToArray();
        RecoveryCoverEvidence[] covers = snapshot.Books.Where(book => affected.Contains(book.Id))
            .Select(book => new RecoveryCoverEvidence(book.Id,
                book.PublicationMetadata.HasCover, null, null)).ToArray();
        return new(snapshot, covers,
            RecoverySnapshotFingerprintPolicy.ComputeFull(snapshot, covers),
            RecoverySnapshotFingerprintPolicy.ComputeAffected(snapshot, affected, covers),
            RecoverySnapshotFingerprintPolicy.ComputeUnrelated(snapshot, affected, covers));
    }

    public static RecoveryPlan PlanFor(
        RecoverySourceInspection source,
        RecoveryCurrentStateSnapshot current,
        RecoveryCapabilityProfile profile,
        Guid? id = null)
    {
        IRecoveryIdGenerator ids = A.Fake<IRecoveryIdGenerator>();
        A.CallTo(() => ids.CreatePlanId()).Returns(new RecoveryPlanId(
            id ?? RecoveryPlanId.Value));
        return new RecoveryPlanGenerator(ids, new RecoveryPlanValidator()).Generate(new(
            source, new CurrentStateReconciler().Reconcile(source, current),
            current.Snapshot.Identity.LibraryRoot, profile), ExecutionTestData.Now);
    }

    public static RecoveryPlan ApprovedPlan()
    {
        RecoveryPlan valid = ValidPlan();
        return RecoveryPlanLifecyclePolicy.Approve(
            valid, valid.Definition.RequiredWarningCodes, ExecutionTestData.Now.AddMinutes(1));
    }
}
