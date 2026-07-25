using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Recoveries;

public sealed class RecoveryPlanGenerator(
    IRecoveryIdGenerator ids,
    IRecoveryPlanValidator validator) : IRecoveryPlanGenerator
{
    public RecoveryPlan Generate(
        GenerateRecoveryPlanRequest request,
        DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Source.IsVerified || request.Source.CleanupPlan is null
            || request.Source.Journal is null || request.Source.OriginalBackupManifest is null
            || request.Source.OriginalManifestFileDigest is null
            || request.Source.SourceToolIdentity is null || request.Source.SourceApplicationVersion is null)
            throw new InvalidOperationException("A recovery plan requires a completely verified source execution.");

        RecoveryPlanId planId = ids.CreatePlanId();
        List<RecoveryIssue> issues = [.. request.Reconciliation.Issues];
        List<RecoveryOperation> operations = [];
        List<ExpectedRecoveredRecordState> expectedRecords = [];
        List<PreservedContentExpectation> preservedContent = [];
        List<(CalibreBookId RecordId, string Format)> absentFormats = [];
        List<CalibreBookId> absentRecords = [];
        CleanupPlan cleanupPlan = request.Source.CleanupPlan;

        foreach (ReconciledRecoveryRecord record in request.Reconciliation.Records)
        {
            ExpectedRecordState original = cleanupPlan.Definition.ExpectedLibraryState.Records.Single(
                value => value.RecordId == record.Identity.OriginalRecordId);
            bool ambiguous = record.Classifications.Contains(RecoveryReconciliationClassification.Ambiguous);
            if (ambiguous)
            {
                AddManual(record, "The affected logical record cannot be matched uniquely.", operations);
                continue;
            }

            CalibreBookId? destinationId = record.Identity.CurrentRecordId;
            RecoveryOperationId? createId = null;
            bool requiresSeparateCopy = record.Formats.Any(value =>
                value.OriginalFingerprint is not null && value.CurrentFingerprint is not null
                && value.OriginalFingerprint != value.CurrentFingerprint)
                || record.Identity.CurrentRecordId is not null
                && record.Classifications.Contains(
                    RecoveryReconciliationClassification.IndependentlyModifiedAfterExecution);
            if (record.Classifications.Contains(RecoveryReconciliationClassification.Missing)
                || requiresSeparateCopy)
            {
                bool canCreate = Require(request.CapabilityProfile, RecoveryCapability.CreateEmptyRecord, issues,
                    record.Identity.LogicalRecordId, "RECOVERY.CREATE_RECORD_UNSUPPORTED");
                bool canFind = Require(request.CapabilityProfile, RecoveryCapability.FindCreatedRecord, issues,
                    record.Identity.LogicalRecordId, "RECOVERY.CREATED_RECORD_LOOKUP_UNSUPPORTED");
                bool canRestoreMetadata = RequireCompleteMetadataProfile(
                    request.CapabilityProfile, issues, record.Identity.LogicalRecordId);
                if (!canCreate || !canFind || !canRestoreMetadata)
                {
                    AddManual(record, "A missing or separate recovered record cannot be created and uniquely rediscovered.", operations);
                    continue;
                }
                createId = new($"construct:create:{record.Identity.LogicalRecordId.Value}");
                operations.Add(Operation(createId, RecoveryOperationKind.CreateRecoveredRecord,
                    RecoveryOperationPhase.Constructive, record, null, null, null, [],
                    "Create a new logical recovery record before restoring its verified content.",
                    RecoveryRiskLevel.Constructive, "CreateEmptyRecord"));
                destinationId = null;
                issues.Add(new("RECOVERY.RECORD_ID_WILL_CHANGE", RecoveryIssueSeverity.AcknowledgementRequired,
                    $"Logical record {record.Identity.LogicalRecordId}",
                    "Calibre may assign a different numeric record ID during semantic recreation.",
                    record.Identity.LogicalRecordId));
                foreach (ReconciledFormat currentFormat in record.Formats.Where(value =>
                             value.CurrentRecordId is not null
                             && value.CurrentFingerprint is not null))
                {
                    AddPreserved(preservedContent, new(record.Identity.LogicalRecordId,
                        currentFormat.CurrentRecordId!.Value, currentFormat.Format,
                        currentFormat.CurrentFingerprint!,
                        "Current record content retained while verified pre-state is restored separately."));
                }
            }

            List<RecoveryOperationId> constructiveForRecord = createId is null ? [] : [createId];
            if (createId is not null)
            {
                VerifiedRecoverySourceArtifact? metadata = request.Source.Artifacts.SingleOrDefault(value =>
                    value.Kind == RecoverySourceArtifactKind.OriginalMetadataOpf
                    && value.RecordId == original.RecordId);
                if (metadata is null)
                {
                    issues.Add(Block("RECOVERY.ORIGINAL_METADATA_BACKUP_MISSING",
                        $"Logical record {record.Identity.LogicalRecordId}",
                        "The original metadata OPF backup is unavailable.", record.Identity.LogicalRecordId));
                }
                else
                {
                    foreach ((RecoveryCapability capability, string field) in SupportedMetadataFields(request.CapabilityProfile))
                    {
                        RecoveryOperationId metadataId = new($"construct:metadata:{field}:{record.Identity.LogicalRecordId.Value}");
                        operations.Add(Operation(metadataId, RecoveryOperationKind.RestoreMetadataFromBackup,
                            RecoveryOperationPhase.Constructive, record, null, metadata.RelativePath, null,
                            [createId], $"Restore the qualified {field} field from verified pre-state metadata.",
                            RecoveryRiskLevel.Constructive, capability.ToString()));
                        constructiveForRecord.Add(metadataId);
                    }
                }
            }

            foreach (ReconciledFormat format in record.Formats)
            {
                if (format.Classification == RecoveryReconciliationClassification.UnexpectedUniqueContent
                    && format.CurrentFingerprint is not null && format.CurrentRecordId is not null)
                {
                    RecoveryOperationId preserveId = new($"preserve:format:{record.Identity.LogicalRecordId.Value}:{format.Format}");
                    operations.Add(Operation(preserveId, RecoveryOperationKind.PreserveUnexpectedCurrentFormat,
                        RecoveryOperationPhase.NonMutating, record, format.Format, null,
                        format.CurrentFingerprint, [], "Back up and retain unexpected unique current content.",
                        RecoveryRiskLevel.None, "NotApplicable"));
                    AddPreserved(preservedContent, new(record.Identity.LogicalRecordId,
                        format.CurrentRecordId.Value, format.Format, format.CurrentFingerprint,
                        "Unexpected unique post-execution content."));
                    continue;
                }

                if (format.OriginalFingerprint is null)
                {
                    if (createId is not null && format.CurrentFingerprint is not null
                        && format.CurrentRecordId is not null)
                    {
                        operations.Add(Operation(new(
                                $"preserve:separate-record:{record.Identity.LogicalRecordId.Value}:{format.Format}"),
                            RecoveryOperationKind.PreserveUnexpectedCurrentFormat,
                            RecoveryOperationPhase.NonMutating, record, format.Format,
                            null, format.CurrentFingerprint, [],
                            "Retain the complete independently modified current record while pre-state is restored separately.",
                            RecoveryRiskLevel.None, "NotApplicable"));
                        continue;
                    }
                    if (format.Classification == RecoveryReconciliationClassification.ReplacedByExecution
                        && format.CurrentRecordId is not null)
                    {
                        if (!Require(request.CapabilityProfile, RecoveryCapability.RemoveCleanupAddedFormat, issues,
                                record.Identity.LogicalRecordId, "RECOVERY.REMOVE_FORMAT_UNSUPPORTED", format.Format))
                            continue;
                        RecoveryOperationId prerequisite = new($"verify:cleanup-format:{record.Identity.LogicalRecordId.Value}:{format.Format}");
                        operations.Add(Operation(prerequisite, RecoveryOperationKind.NoActionAlreadyRestored,
                            RecoveryOperationPhase.NonMutating, record, null, null, null, [],
                            "Verify the format is exactly the cleanup-added payload before destructive removal.",
                            RecoveryRiskLevel.None, "VerifyRestoredContent"));
                        RecoveryOperationId remove = new($"destroy:format:{record.Identity.LogicalRecordId.Value}:{format.Format}");
                        operations.Add(Operation(remove, RecoveryOperationKind.RemoveFormatAddedByExecution,
                            RecoveryOperationPhase.Destructive, record, format.Format, null,
                            format.CurrentFingerprint, [prerequisite],
                            "Remove a format proven to have been added solely by the source cleanup execution.",
                            RecoveryRiskLevel.Destructive, RecoveryCapability.RemoveCleanupAddedFormat.ToString()));
                        absentFormats.Add((format.CurrentRecordId.Value, format.Format));
                    }
                    continue;
                }

                bool originalAlreadyPresent = format.CurrentFingerprint == format.OriginalFingerprint;
                if (originalAlreadyPresent && createId is null)
                {
                    operations.Add(Operation(new($"noop:format:{record.Identity.LogicalRecordId.Value}:{format.Format}"),
                        RecoveryOperationKind.NoActionAlreadyRestored, RecoveryOperationPhase.NonMutating,
                        record, null, null, format.OriginalFingerprint, [],
                        "The original format bytes are already present and require no mutation.",
                        RecoveryRiskLevel.None, "VerifyRestoredContent", format.Format));
                    continue;
                }

                RecoveryCapability required = createId is not null
                    ? RecoveryCapability.AddBackedUpFormat
                    : format.CurrentFingerprint is null
                    ? RecoveryCapability.AddBackedUpFormat
                    : requiresSeparateCopy
                        ? RecoveryCapability.AddBackedUpFormat
                        : RecoveryCapability.ReplaceExistingFormat;
                if (!Require(request.CapabilityProfile, required, issues,
                        record.Identity.LogicalRecordId, "RECOVERY.RESTORE_FORMAT_UNSUPPORTED", format.Format))
                    continue;
                VerifiedRecoverySourceArtifact? backup = request.Source.FindOriginalFormat(
                    record.Identity.OriginalRecordId, format.Format);
                if (backup is null)
                {
                    issues.Add(Block("RECOVERY.ORIGINAL_FORMAT_BACKUP_MISSING",
                        $"Logical record {record.Identity.LogicalRecordId} / {format.Format}",
                        "The exact original format backup is missing.", record.Identity.LogicalRecordId, format.Format));
                    continue;
                }
                RecoveryOperationId restoreId = new($"construct:format:{record.Identity.LogicalRecordId.Value}:{format.Format}");
                operations.Add(Operation(restoreId, RecoveryOperationKind.RestoreFormatFromBackup,
                    RecoveryOperationPhase.Constructive, record, format.Format, backup.RelativePath,
                    format.OriginalFingerprint, constructiveForRecord,
                    requiresSeparateCopy
                        ? "Restore the original bytes into a separate recovered record so current same-format bytes remain untouched."
                        : "Restore missing original format bytes from the verified cleanup backup.",
                    RecoveryRiskLevel.Constructive, required.ToString()));
                constructiveForRecord.Add(restoreId);
                if (format.PreserveCurrentContent && format.CurrentFingerprint is not null
                    && format.CurrentRecordId is not null)
                    AddPreserved(preservedContent, new(record.Identity.LogicalRecordId,
                        format.CurrentRecordId.Value, format.Format, format.CurrentFingerprint,
                        "Conflicting current same-format variant."));
            }

            expectedRecords.Add(new(record.Identity.LogicalRecordId, original,
                destinationId, SupportedMetadataFields(request.CapabilityProfile).Select(value => value.Field).ToArray()));
        }

        if (issues.Any(value => value.Severity == RecoveryIssueSeverity.Blocking))
        {
            operations = request.Reconciliation.Records.Select(record =>
                Operation(new($"manual:{record.Identity.LogicalRecordId.Value}"),
                    RecoveryOperationKind.ManualInterventionRequired,
                    RecoveryOperationPhase.NonMutating, record, null, null, null, [],
                    "Blocking integrity, identity, preservation, or capability issues require manual intervention.",
                    RecoveryRiskLevel.Manual, "NotApplicable")).ToList();
            expectedRecords.Clear();
            preservedContent.Clear();
            absentFormats.Clear();
            absentRecords.Clear();
        }
        else
        {
            RecoveryOperationId[] constructiveAndPreservation = operations
                .Where(value => value.Phase != RecoveryOperationPhase.Destructive)
                .Select(value => value.Id).ToArray();
            if (operations.Any(value => value.Phase == RecoveryOperationPhase.Destructive))
            {
                RecoveryOperationId barrier = new("verify:intermediate-barrier");
                ReconciledRecoveryRecord subject = request.Reconciliation.Records[0];
                operations.Add(Operation(barrier, RecoveryOperationKind.NoActionAlreadyRestored,
                    RecoveryOperationPhase.NonMutating, subject, null, null, null,
                    constructiveAndPreservation,
                    "Fresh intermediate verification barrier before destructive recovery.",
                    RecoveryRiskLevel.None, "VerifyRestoredContent"));
                operations = operations.Select(value => value.Phase != RecoveryOperationPhase.Destructive
                    ? value
                    : CloneWithDependencies(value, value.DependencyIds.Append(barrier))).ToList();
            }
        }

        RecoveryOperationGraph graph = new(operations);
        RecoveryInputIdentity input = new(cleanupPlan.Id, cleanupPlan.SchemaVersion,
            cleanupPlan.ArtifactRevision, cleanupPlan.ContentDigest, request.Source.Journal.ExecutionId,
            request.Source.Journal.SchemaVersion, request.Source.Journal.FileDigest,
            request.Source.Journal.FinalEntryHash, request.Source.Journal.TerminalSummaryDigest,
            VerifiedBackupManifest.Version, request.Source.OriginalBackupManifest.ManifestDigest,
            request.Source.OriginalManifestFileDigest.Value, request.Source.SourceApplicationVersion,
            request.Source.SourceToolIdentity, request.Reconciliation.CurrentLibraryUuid,
            request.Reconciliation.CurrentLibrarySchemaVersion,
            RecoverySnapshotFingerprintPolicy.ComputeCanonicalRootIdentity(request.CanonicalLibraryRootIdentity),
            request.Reconciliation.FullStateFingerprint, request.Reconciliation.AffectedStateFingerprint,
            request.Reconciliation.UnrelatedStateFingerprint, request.Reconciliation.Version,
            request.Reconciliation.Digest, request.CapabilityProfile.ProfileIdentity);
        RecoveryBackupChain chain = new(cleanupPlan.Id, cleanupPlan.ContentDigest,
            request.Source.Journal.ExecutionId, request.Source.Journal.FileDigest,
            request.Source.Journal.FinalEntryHash, request.Source.OriginalManifestFileDigest.Value,
            request.Source.OriginalBackupManifest.ManifestDigest, planId, null);
        RecoveryPlanDefinition definition = new(input,
            new(cleanupPlan.Id, request.Source.Journal.ExecutionId, generatedAtUtc,
                request.Source.Journal.LastDisposition.ToString(), request.Source.BundleIdentity),
            chain, request.Reconciliation, graph,
            new(expectedRecords, preservedContent, absentFormats, absentRecords,
                request.Reconciliation.UnrelatedStateFingerprint), issues);
        RecoveryPlanValidationResult validation = validator.Validate(definition, generatedAtUtc);
        return RecoveryPlanLifecyclePolicy.Create(planId, definition, validation, generatedAtUtc);
    }

    private static RecoveryOperation Operation(
        RecoveryOperationId id,
        RecoveryOperationKind kind,
        RecoveryOperationPhase phase,
        ReconciledRecoveryRecord record,
        string? format,
        string? backupArtifact,
        FormatFileFingerprint? fingerprint,
        IEnumerable<RecoveryOperationId> dependencies,
        string reason,
        RecoveryRiskLevel risk,
        string capability,
        string? verificationFormat = null) =>
        new(id, kind, phase, record.Identity.LogicalRecordId, record.Identity.OriginalRecordId,
            record.Identity.CurrentRecordId, record.Identity.RecoveredRecordId, format,
            backupArtifact, kind is RecoveryOperationKind.RestoreFormatFromBackup
                or RecoveryOperationKind.CreateRecoveryCopyInsteadOfOverwrite ? fingerprint : null,
            kind is RecoveryOperationKind.RemoveFormatAddedByExecution ? fingerprint : null,
            dependencies,
            new($"RECOVERY.VERIFY.{kind.ToString().ToUpperInvariant()}",
                record.Identity.LogicalRecordId, reason, record.Identity.CurrentRecordId,
                verificationFormat ?? format,
                (verificationFormat ?? format) is not null ? fingerprint : null,
                kind is not (RecoveryOperationKind.RemoveFormatAddedByExecution
                    or RecoveryOperationKind.RemoveRecordCreatedByExecution)),
            reason, "Verified source journal projection.", risk, [], capability);

    private static RecoveryOperation CloneWithDependencies(
        RecoveryOperation value,
        IEnumerable<RecoveryOperationId> dependencies) =>
        new(value.Id, value.Kind, value.Phase, value.LogicalRecordId, value.OriginalRecordId,
            value.CurrentRecordId, value.ProposedRecoveredRecordId, value.Format,
            value.OriginalBackupArtifactIdentity, value.OriginalBackupFingerprint,
            value.ExpectedCurrentFingerprint, dependencies, value.Verification,
            value.Reason, value.SourceJournalEvidence, value.Risk,
            value.PreservationDependencyIds, value.RequiredCapability);

    private static void AddManual(
        ReconciledRecoveryRecord record,
        string reason,
        List<RecoveryOperation> operations) =>
        operations.Add(Operation(new($"manual:{record.Identity.LogicalRecordId.Value}"),
            RecoveryOperationKind.ManualInterventionRequired, RecoveryOperationPhase.NonMutating,
            record, null, null, null, [], reason, RecoveryRiskLevel.Manual, "NotApplicable"));

    private static void AddPreserved(
        List<PreservedContentExpectation> values,
        PreservedContentExpectation value)
    {
        if (!values.Any(existing => existing.CurrentRecordId == value.CurrentRecordId
            && existing.Format == value.Format
            && existing.Fingerprint == value.Fingerprint))
            values.Add(value);
    }

    private static bool Require(
        RecoveryCapabilityProfile profile,
        RecoveryCapability capability,
        List<RecoveryIssue> issues,
        LogicalRecoveryRecordId logicalId,
        string code,
        string? format = null)
    {
        if (profile.Supports(capability)) return true;
        issues.Add(Block(code, $"Logical record {logicalId}{(format is null ? string.Empty : $" / {format}")}",
            $"The exact recovery profile does not enable {capability}.", logicalId, format));
        return false;
    }

    private static (RecoveryCapability Capability, string Field)[] SupportedMetadataFields(
        RecoveryCapabilityProfile profile)
        => MetadataFields().Where(value => profile.Supports(value.Capability)).ToArray();

    private static bool RequireCompleteMetadataProfile(
        RecoveryCapabilityProfile profile,
        List<RecoveryIssue> issues,
        LogicalRecoveryRecordId logicalId)
    {
        bool supported = true;
        foreach ((RecoveryCapability capability, string field) in MetadataFields())
        {
            if (profile.Supports(capability)) continue;
            supported = false;
            issues.Add(Block("RECOVERY.COMPLETE_METADATA_RESTORE_UNSUPPORTED",
                $"Logical record {logicalId} / {field}",
                $"A recreated record requires qualified restoration of {field}, including explicit empty values.",
                logicalId));
        }
        return supported;
    }

    private static (RecoveryCapability Capability, string Field)[] MetadataFields() =>
    [
        (RecoveryCapability.RestoreTitle, "title"),
        (RecoveryCapability.RestoreAuthors, "authors"),
        (RecoveryCapability.RestoreAuthorSort, "author_sort"),
        (RecoveryCapability.RestorePublisher, "publisher"),
        (RecoveryCapability.RestorePublicationDate, "pubdate"),
        (RecoveryCapability.RestoreLanguages, "languages"),
        (RecoveryCapability.RestoreIdentifiers, "identifiers"),
        (RecoveryCapability.RestoreSeries, "series"),
        (RecoveryCapability.RestoreSeriesIndex, "series_index"),
    ];

    private static RecoveryIssue Block(
        string code,
        string subject,
        string explanation,
        LogicalRecoveryRecordId? logicalId = null,
        string? format = null) =>
        new(code, RecoveryIssueSeverity.Blocking, subject, explanation,
            logicalId, format: format);
}
