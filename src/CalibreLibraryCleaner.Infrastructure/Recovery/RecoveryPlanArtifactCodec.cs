using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Infrastructure.Recovery;

internal static class RecoveryPlanArtifactCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public static byte[] Serialize(RecoveryPlan plan)
    {
        ArtifactDto artifact = ToDto(plan);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, Options);
        return new UTF8Encoding(false).GetBytes(
            Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal)
                .TrimEnd('\n') + "\n");
    }

    public static RecoveryPlan Deserialize(byte[] bytes)
    {
        ArtifactDto artifact = JsonSerializer.Deserialize<ArtifactDto>(bytes, Options)
            ?? throw new JsonException("The recovery plan is empty.");
        if (artifact.SchemaVersion != RecoveryPlanSchemaVersion.V1.Value
            || artifact.ModelVersion != RecoveryModelVersion.V1.Value
            || artifact.PolicyVersion != RecoveryPolicyVersion.V1.Value)
            throw new JsonException("The recovery plan schema, model, or policy is unsupported.");
        RecoveryInputIdentity input = FromDto(artifact.Definition.Input);
        CurrentStateReconciliation reconciliation = FromDto(artifact.Definition.Reconciliation);
        RecoveryPlanId planId = new(Guid.Parse(artifact.RecoveryPlanId));
        RecoveryBackupChain chain = FromDto(artifact.Definition.OriginalBackupChain, planId);
        RecoveryPlanDefinition definition = new(input,
            FromDto(artifact.Definition.Provenance),
            chain, reconciliation,
            new(artifact.Definition.Operations.Select(FromDto)),
            FromDto(artifact.Definition.ExpectedFinalState),
            artifact.Definition.Issues.Select(FromDto));
        RecoveryPlanValidationResult validation = new(
            artifact.Validation.Issues.Select(FromDto),
            artifact.Validation.ValidatedAtUtc, input);
        return new(planId, new(artifact.SchemaVersion), new(artifact.ModelVersion),
            new(artifact.PolicyVersion), new(artifact.ArtifactRevision),
            artifact.State, new(artifact.ContentDigest), artifact.CreatedAtUtc,
            definition, validation,
            artifact.Approval is null ? null : FromDto(artifact.Approval),
            artifact.Revocation is null ? null : FromDto(artifact.Revocation),
            artifact.Completion is null ? null : FromDto(artifact.Completion),
            artifact.Lifecycle.Select(FromDto));
    }

    private static ArtifactDto ToDto(RecoveryPlan plan) => new(
        plan.SchemaVersion.Value, plan.ModelVersion.Value, plan.PolicyVersion.Value,
        plan.Id.ToString(), plan.ArtifactRevision.Value, plan.State,
        plan.ContentDigest.Value, plan.CreatedAtUtc,
        new(ToDto(plan.Definition.InputIdentity), ToDto(plan.Definition.Provenance),
            ToDto(plan.Definition.OriginalBackupChain),
            ToDto(plan.Definition.Reconciliation),
            plan.Definition.OperationGraph.Operations.Select(ToDto).ToArray(),
            ToDto(plan.Definition.ExpectedFinalState),
            plan.Definition.Issues.Select(ToDto).ToArray()),
        new(plan.Validation.ValidatedAtUtc,
            plan.Validation.Issues.Select(ToDto).ToArray()),
        plan.Approval is null ? null : ToDto(plan.Approval),
        plan.Revocation is null ? null : ToDto(plan.Revocation),
        plan.Completion is null ? null : ToDto(plan.Completion),
        plan.LifecycleHistory.Select(ToDto).ToArray());

    private static InputDto ToDto(RecoveryInputIdentity value) => new(
        value.SourcePlanId.ToString(), value.SourcePlanSchemaVersion.Value,
        value.SourcePlanRevision.Value, value.SourcePlanContentDigest.Value,
        value.SourceExecutionId.ToString(), value.SourceJournalSchema,
        value.SourceJournalFileDigest.Value, value.SourceJournalFinalEntryHash,
        value.SourceTerminalSummaryDigest?.Value, value.OriginalManifestSchema,
        value.OriginalManifestInternalDigest.Value, value.OriginalManifestFileDigest.Value,
        value.SourceApplicationVersion,
        value.SourceToolIdentity.CanonicalExecutableIdentity,
        value.SourceToolIdentity.ProductVersion,
        value.SourceToolIdentity.ExecutableSha256.Value,
        value.SourceToolIdentity.CapabilityProfile,
        value.CurrentLibraryUuid, value.CurrentLibrarySchemaVersion,
        value.CanonicalRootIdentityDigest.Value, value.FullStateFingerprint.Value,
        value.AffectedStateFingerprint.Value, value.UnrelatedStateFingerprint.Value,
        value.ReconciliationVersion.Value, value.ReconciliationDigest.Value,
        value.RecoveryCapabilityProfile);

    private static RecoveryInputIdentity FromDto(InputDto value) => new(
        new(Guid.Parse(value.SourcePlanId)), new(value.SourcePlanSchemaVersion),
        new(value.SourcePlanRevision), new(value.SourcePlanContentDigest),
        new(Guid.Parse(value.SourceExecutionId)), value.SourceJournalSchema,
        new(value.SourceJournalFileDigest), value.SourceJournalFinalEntryHash,
        value.SourceTerminalSummaryDigest is null ? null : new(value.SourceTerminalSummaryDigest),
        value.OriginalManifestSchema, new(value.OriginalManifestInternalDigest),
        new(value.OriginalManifestFileDigest), value.SourceApplicationVersion,
        new(value.SourceToolPath, value.SourceToolVersion,
            new(value.SourceToolDigest), value.SourceToolProfile),
        value.CurrentLibraryUuid, value.CurrentLibrarySchemaVersion,
        new(value.CanonicalRootIdentityDigest), new(value.FullStateFingerprint),
        new(value.AffectedStateFingerprint), new(value.UnrelatedStateFingerprint),
        new(value.ReconciliationVersion), new(value.ReconciliationDigest),
        value.RecoveryCapabilityProfile);

    private static ProvenanceDto ToDto(RecoveryProvenance value) => new(
        value.SourceCleanupPlanId.ToString(), value.SourceExecutionId.ToString(),
        value.ReconciledAtUtc, value.SourceDisposition, value.SourceBundleIdentity);

    private static RecoveryProvenance FromDto(ProvenanceDto value) => new(
        new(Guid.Parse(value.SourceCleanupPlanId)),
        new(Guid.Parse(value.SourceExecutionId)), value.ReconciledAtUtc,
        value.SourceDisposition, value.SourceBundleIdentity);

    private static BackupChainDto ToDto(RecoveryBackupChain value) => new(
        value.SourcePlanId.ToString(), value.SourcePlanContentDigest.Value,
        value.SourceExecutionId.ToString(), value.SourceJournalFileDigest.Value,
        value.SourceJournalFinalEntryHash, value.OriginalManifestFileDigest.Value,
        value.OriginalManifestInternalDigest.Value,
        value.RecoveryPlanContentDigest?.Value,
        value.RecoveryExecutionId?.ToString(),
        value.CurrentStateManifestFileDigest?.Value,
        value.CurrentStateManifestInternalDigest?.Value,
        value.RecoveryJournalIdentity);

    private static RecoveryBackupChain FromDto(BackupChainDto value, RecoveryPlanId planId) => new(
        new(Guid.Parse(value.SourcePlanId)), new(value.SourcePlanContentDigest),
        new(Guid.Parse(value.SourceExecutionId)), new(value.SourceJournalFileDigest),
        value.SourceJournalFinalEntryHash, new(value.OriginalManifestFileDigest),
        new(value.OriginalManifestInternalDigest), planId,
        value.RecoveryPlanContentDigest is null ? null : new(value.RecoveryPlanContentDigest),
        value.RecoveryExecutionId is null ? null : new(Guid.Parse(value.RecoveryExecutionId)),
        value.CurrentStateManifestFileDigest is null ? null : new(value.CurrentStateManifestFileDigest),
        value.CurrentStateManifestInternalDigest is null ? null : new(value.CurrentStateManifestInternalDigest),
        value.RecoveryJournalIdentity);

    private static ReconciliationDto ToDto(CurrentStateReconciliation value) => new(
        value.Version.Value, value.SourceExecutionIdentity, value.CurrentLibraryUuid,
        value.CurrentLibrarySchemaVersion, value.FullStateFingerprint.Value,
        value.AffectedStateFingerprint.Value, value.UnrelatedStateFingerprint.Value,
        value.SourceOperations.Select(ToDto).ToArray(),
        value.Records.Select(ToDto).ToArray(),
        value.Issues.Select(ToDto).ToArray(), value.Digest.Value);

    private static CurrentStateReconciliation FromDto(ReconciliationDto value) => new(
        new(value.Version), value.SourceExecutionIdentity, value.CurrentLibraryUuid,
        value.CurrentLibrarySchemaVersion, new(value.FullStateFingerprint),
        new(value.AffectedStateFingerprint), new(value.UnrelatedStateFingerprint),
        value.SourceOperations.Select(FromDto), value.Records.Select(FromDto),
        value.Issues.Select(FromDto), new(value.Digest));

    private static SourceOperationDto ToDto(SourceOperationProjection value) => new(
        value.OperationId.Value, value.Kind, value.State, value.TargetRecordId.Value,
        value.SourceRecordId?.Value, value.Format,
        value.DependencyIds.Select(id => id.Value).ToArray());

    private static SourceOperationProjection FromDto(SourceOperationDto value) => new(
        new(value.OperationId), value.Kind, value.State, new(value.TargetRecordId),
        value.SourceRecordId is null ? null : new(value.SourceRecordId.Value),
        value.Format, value.DependencyIds.Select(id => new ExecutionOperationId(id)).ToArray());

    private static ReconciledRecordDto ToDto(ReconciledRecoveryRecord value) => new(
        ToDto(value.Identity), value.Classifications.ToArray(),
        value.Formats.Select(ToDto).ToArray(),
        value.AmbiguousCandidateIds.Select(id => id.Value).ToArray());

    private static ReconciledRecoveryRecord FromDto(ReconciledRecordDto value) => new(
        FromDto(value.Identity), value.Classifications,
        value.Formats.Select(FromDto),
        value.AmbiguousCandidateIds.Select(id => new CalibreBookId(id)));

    private static RecordIdentityDto ToDto(RecoveryRecordIdentity value) => new(
        value.LogicalRecordId.Value, value.OriginalRecordId.Value,
        value.CurrentRecordId?.Value, value.RecoveredRecordId?.Value,
        value.MetadataFingerprint, value.CurrentMetadataFingerprint, value.Identifiers.ToArray(),
        value.BackedUpFormats.Select(ToDto).ToArray(),
        value.OriginalBackupArtifactIdentity, value.PreservedSeparateRecordId?.Value);

    private static RecoveryRecordIdentity FromDto(RecordIdentityDto value) => new(
        new(value.LogicalRecordId), new(value.OriginalRecordId),
        value.CurrentRecordId is null ? null : new(value.CurrentRecordId.Value),
        value.RecoveredRecordId is null ? null : new(value.RecoveredRecordId.Value),
        value.MetadataFingerprint, value.CurrentMetadataFingerprint, value.Identifiers,
        value.BackedUpFormats.Select(FromDto), value.OriginalBackupArtifactIdentity,
        value.PreservedSeparateRecordId is null ? null : new(value.PreservedSeparateRecordId.Value));

    private static FormatIdentityDto ToDto(RecoveryFormatIdentity value) => new(
        value.Format, ToDto(value.Fingerprint), value.BackupArtifactIdentity);

    private static RecoveryFormatIdentity FromDto(FormatIdentityDto value) => new(
        value.Format, FromDto(value.Fingerprint), value.BackupArtifactIdentity);

    private static ReconciledFormatDto ToDto(ReconciledFormat value) => new(
        value.LogicalRecordId.Value, value.Format, value.Classification,
        value.OriginalFingerprint is null ? null : ToDto(value.OriginalFingerprint),
        value.ExpectedPostFingerprint is null ? null : ToDto(value.ExpectedPostFingerprint),
        value.CurrentFingerprint is null ? null : ToDto(value.CurrentFingerprint),
        value.CurrentRecordId?.Value, value.OriginalBackupArtifactIdentity,
        value.PreserveCurrentContent, value.SourceOperationId?.Value);

    private static ReconciledFormat FromDto(ReconciledFormatDto value) => new(
        new(value.LogicalRecordId), value.Format, value.Classification,
        value.OriginalFingerprint is null ? null : FromDto(value.OriginalFingerprint),
        value.ExpectedPostFingerprint is null ? null : FromDto(value.ExpectedPostFingerprint),
        value.CurrentFingerprint is null ? null : FromDto(value.CurrentFingerprint),
        value.CurrentRecordId is null ? null : new(value.CurrentRecordId.Value),
        value.OriginalBackupArtifactIdentity, value.PreserveCurrentContent,
        value.SourceOperationId is null ? null : new(value.SourceOperationId));

    private static OperationDto ToDto(RecoveryOperation value) => new(
        value.Id.Value, value.Kind, value.Phase, value.LogicalRecordId.Value,
        value.OriginalRecordId.Value, value.CurrentRecordId?.Value,
        value.ProposedRecoveredRecordId?.Value, value.Format,
        value.OriginalBackupArtifactIdentity,
        value.OriginalBackupFingerprint is null ? null : ToDto(value.OriginalBackupFingerprint),
        value.ExpectedCurrentFingerprint is null ? null : ToDto(value.ExpectedCurrentFingerprint),
        value.DependencyIds.Select(id => id.Value).ToArray(),
        ToDto(value.Verification), value.Reason, value.SourceJournalEvidence,
        value.Risk, value.PreservationDependencyIds.Select(id => id.Value).ToArray(),
        value.RequiredCapability);

    private static RecoveryOperation FromDto(OperationDto value) => new(
        new(value.OperationId), value.Kind, value.Phase,
        new(value.LogicalRecordId), new(value.OriginalRecordId),
        value.CurrentRecordId is null ? null : new(value.CurrentRecordId.Value),
        value.ProposedRecoveredRecordId is null ? null : new(value.ProposedRecoveredRecordId.Value),
        value.Format, value.OriginalBackupArtifactIdentity,
        value.OriginalBackupFingerprint is null ? null : FromDto(value.OriginalBackupFingerprint),
        value.ExpectedCurrentFingerprint is null ? null : FromDto(value.ExpectedCurrentFingerprint),
        value.DependencyIds.Select(id => new RecoveryOperationId(id)),
        FromDto(value.Verification), value.Reason, value.SourceJournalEvidence,
        value.Risk, value.PreservationDependencyIds.Select(id => new RecoveryOperationId(id)),
        value.RequiredCapability);

    private static VerificationDto ToDto(RecoveryVerificationExpectation value) => new(
        value.Code, value.LogicalRecordId.Value, value.Description,
        value.ExpectedCurrentRecordId?.Value, value.Format,
        value.ExpectedFingerprint is null ? null : ToDto(value.ExpectedFingerprint),
        value.ExpectedPresent);

    private static RecoveryVerificationExpectation FromDto(VerificationDto value) => new(
        value.Code, new(value.LogicalRecordId), value.Description,
        value.ExpectedCurrentRecordId is null ? null : new(value.ExpectedCurrentRecordId.Value),
        value.Format, value.ExpectedFingerprint is null ? null : FromDto(value.ExpectedFingerprint),
        value.ExpectedPresent);

    private static ExpectedStateDto ToDto(ExpectedRecoveredState value) => new(
        value.Records.Select(record => new ExpectedRecoveredRecordDto(
            record.LogicalRecordId.Value, ToDto(record.OriginalState),
            record.ExpectedCurrentRecordId?.Value, record.SupportedMetadataFields.ToArray())).ToArray(),
        value.PreservedContent.Select(item => new PreservedDto(
            item.LogicalRecordId.Value, item.CurrentRecordId.Value, item.Format,
            ToDto(item.Fingerprint), item.PreservationReason)).ToArray(),
        value.ExpectedAbsentFormats.Select(item =>
            new AbsentFormatDto(item.RecordId.Value, item.Format)).ToArray(),
        value.ExpectedAbsentRecords.Select(id => id.Value).ToArray(),
        value.UnrelatedStateFingerprint.Value);

    private static ExpectedRecoveredState FromDto(ExpectedStateDto value) => new(
        value.Records.Select(record => new ExpectedRecoveredRecordState(
            new(record.LogicalRecordId), FromDto(record.OriginalState),
            record.ExpectedCurrentRecordId is null ? null : new(record.ExpectedCurrentRecordId.Value),
            record.SupportedMetadataFields)),
        value.PreservedContent.Select(item => new PreservedContentExpectation(
            new(item.LogicalRecordId), new(item.CurrentRecordId), item.Format,
            FromDto(item.Fingerprint), item.PreservationReason)),
        value.ExpectedAbsentFormats.Select(item =>
            (new CalibreBookId(item.RecordId), item.Format)),
        value.ExpectedAbsentRecords.Select(id => new CalibreBookId(id)),
        new(value.UnrelatedStateFingerprint));

    private static ExpectedRecordDto ToDto(ExpectedRecordState value) => new(
        value.RecordId.Value, value.Title, value.AuthorSort,
        value.Authors.Select(author =>
            new AuthorDto(author.Id.Value, author.Name, author.SortName)).ToArray(),
        value.Identifiers.Select(identifier =>
            new IdentifierDto(identifier.Type, identifier.Value)).ToArray(),
        value.Publisher, value.PublicationDate, value.Series, value.SeriesIndex,
        value.Languages.ToArray(), value.HasCover, value.RelativeDirectory,
        value.Formats.Select(ToDto).ToArray());

    private static ExpectedRecordState FromDto(ExpectedRecordDto value) => new(
        new(value.RecordId), value.Title, value.AuthorSort,
        value.Authors.Select(author => new ExpectedAuthorState(
            new(author.Id), author.Name, author.SortName)),
        value.Identifiers.Select(identifier =>
            new ExpectedIdentifierState(identifier.Type, identifier.Value)),
        value.Publisher, value.PublicationDate, value.Series, value.SeriesIndex,
        value.Languages, value.HasCover, value.RelativeDirectory,
        value.Formats.Select(FromDto));

    private static ExpectedFormatDto ToDto(ExpectedFormatState value) => new(
        value.RecordId.Value, value.Format, value.StoredFileName, value.RelativePath,
        value.Status, ToDto(value.Fingerprint),
        value.Observation.Length, value.Observation.CreationTimeUtc,
        value.Observation.LastWriteTimeUtc, value.Observation.Attributes);

    private static ExpectedFormatState FromDto(ExpectedFormatDto value) => new(
        new(value.RecordId), value.Format, value.StoredFileName, value.RelativePath,
        value.Status, FromDto(value.Fingerprint), new(value.Length,
            value.CreationTimeUtc, value.LastWriteTimeUtc, value.Attributes));

    private static FingerprintDto ToDto(FormatFileFingerprint value) =>
        new(value.SizeInBytes, value.Sha256.Value);

    private static FormatFileFingerprint FromDto(FingerprintDto value) =>
        new(value.SizeInBytes, new(value.Sha256));

    private static IssueDto ToDto(RecoveryIssue value) => new(
        value.Code, value.Severity, value.Subject, value.Explanation,
        value.LogicalRecordId?.Value, value.CurrentRecordId?.Value,
        value.Format, new Dictionary<string, string>(value.Evidence, StringComparer.Ordinal));

    private static RecoveryIssue FromDto(IssueDto value) => new(
        value.Code, value.Severity, value.Subject, value.Explanation,
        value.LogicalRecordId is null ? null : new(value.LogicalRecordId),
        value.CurrentRecordId is null ? null : new(value.CurrentRecordId.Value),
        value.Format, value.Evidence);

    private static ApprovalDto ToDto(RecoveryApproval value) => new(
        value.PlanId.ToString(), value.ApprovedAtUtc, value.ApprovedRevision.Value, value.ContentDigest.Value,
        value.SourceExecutionId.ToString(), value.CurrentLibraryUuid,
        value.CanonicalRootIdentityDigest.Value, value.CurrentStateFingerprint.Value,
        value.CapabilityProfile, value.AcknowledgedWarningCodes.ToArray());

    private static RecoveryApproval FromDto(ApprovalDto value) => new(
        new(Guid.Parse(value.RecoveryPlanId)), value.ApprovedAtUtc,
        new(value.ApprovedRevision), new(value.ContentDigest),
        new(Guid.Parse(value.SourceExecutionId)), value.CurrentLibraryUuid,
        new(value.CanonicalRootIdentityDigest), new(value.CurrentStateFingerprint),
        value.CapabilityProfile, value.AcknowledgedWarningCodes);

    private static RevocationDto ToDto(RecoveryRevocation value) => new(
        value.RevokedAtUtc, value.Reason, value.PriorApprovalRevision?.Value,
        value.ContentDigest.Value);

    private static RecoveryRevocation FromDto(RevocationDto value) => new(
        value.RevokedAtUtc, value.Reason,
        value.PriorApprovalRevision is null ? null : new(value.PriorApprovalRevision.Value),
        new(value.ContentDigest));

    private static CompletionDto ToDto(RecoveryPlanCompletion value) => new(
        value.RecoveryExecutionId.ToString(), value.CompletedAtUtc,
        value.RecoveryJournalFinalHash, value.ChangedRecordIds,
        value.PreservedUnexpectedContent);

    private static RecoveryPlanCompletion FromDto(CompletionDto value) => new(
        new(Guid.Parse(value.RecoveryExecutionId)), value.CompletedAtUtc,
        value.RecoveryJournalFinalHash, value.ChangedRecordIds,
        value.PreservedUnexpectedContent);

    private static LifecycleDto ToDto(RecoveryPlanLifecycleEntry value) => new(
        value.Revision.Value, value.FromState, value.ToState,
        value.ChangedAtUtc, value.Reason);

    private static RecoveryPlanLifecycleEntry FromDto(LifecycleDto value) => new(
        new(value.Revision), value.FromState, value.ToState,
        value.ChangedAtUtc, value.Reason);

    private sealed record ArtifactDto(
        string SchemaVersion, string ModelVersion, string PolicyVersion,
        string RecoveryPlanId, int ArtifactRevision, RecoveryPlanState State,
        string ContentDigest, DateTimeOffset CreatedAtUtc, DefinitionDto Definition,
        ValidationDto Validation, ApprovalDto? Approval, RevocationDto? Revocation,
        CompletionDto? Completion, LifecycleDto[] Lifecycle);
    private sealed record DefinitionDto(
        InputDto Input, ProvenanceDto Provenance, BackupChainDto OriginalBackupChain,
        ReconciliationDto Reconciliation, OperationDto[] Operations,
        ExpectedStateDto ExpectedFinalState, IssueDto[] Issues);
    private sealed record InputDto(
        string SourcePlanId, string SourcePlanSchemaVersion, int SourcePlanRevision,
        string SourcePlanContentDigest, string SourceExecutionId, string SourceJournalSchema,
        string SourceJournalFileDigest, string SourceJournalFinalEntryHash,
        string? SourceTerminalSummaryDigest, string OriginalManifestSchema,
        string OriginalManifestInternalDigest, string OriginalManifestFileDigest,
        string SourceApplicationVersion, string SourceToolPath, string SourceToolVersion,
        string SourceToolDigest, string SourceToolProfile, string CurrentLibraryUuid,
        int CurrentLibrarySchemaVersion, string CanonicalRootIdentityDigest,
        string FullStateFingerprint, string AffectedStateFingerprint,
        string UnrelatedStateFingerprint, string ReconciliationVersion,
        string ReconciliationDigest, string RecoveryCapabilityProfile);
    private sealed record ProvenanceDto(
        string SourceCleanupPlanId, string SourceExecutionId,
        DateTimeOffset ReconciledAtUtc, string SourceDisposition,
        string SourceBundleIdentity);
    private sealed record BackupChainDto(
        string SourcePlanId, string SourcePlanContentDigest, string SourceExecutionId,
        string SourceJournalFileDigest, string SourceJournalFinalEntryHash,
        string OriginalManifestFileDigest, string OriginalManifestInternalDigest,
        string? RecoveryPlanContentDigest, string? RecoveryExecutionId,
        string? CurrentStateManifestFileDigest, string? CurrentStateManifestInternalDigest,
        string? RecoveryJournalIdentity);
    private sealed record ReconciliationDto(
        string Version, string SourceExecutionIdentity, string CurrentLibraryUuid,
        int CurrentLibrarySchemaVersion, string FullStateFingerprint,
        string AffectedStateFingerprint, string UnrelatedStateFingerprint,
        SourceOperationDto[] SourceOperations, ReconciledRecordDto[] Records,
        IssueDto[] Issues, string Digest);
    private sealed record SourceOperationDto(
        string OperationId, ExecutionOperationKind Kind, DurableSourceOperationState State,
        long TargetRecordId, long? SourceRecordId, string? Format, string[] DependencyIds);
    private sealed record ReconciledRecordDto(
        RecordIdentityDto Identity, RecoveryReconciliationClassification[] Classifications,
        ReconciledFormatDto[] Formats, long[] AmbiguousCandidateIds);
    private sealed record RecordIdentityDto(
        string LogicalRecordId, long OriginalRecordId, long? CurrentRecordId,
        long? RecoveredRecordId, string MetadataFingerprint,
        string? CurrentMetadataFingerprint, string[] Identifiers,
        FormatIdentityDto[] BackedUpFormats, string OriginalBackupArtifactIdentity,
        long? PreservedSeparateRecordId);
    private sealed record FormatIdentityDto(
        string Format, FingerprintDto Fingerprint, string BackupArtifactIdentity);
    private sealed record ReconciledFormatDto(
        string LogicalRecordId, string Format,
        RecoveryReconciliationClassification Classification,
        FingerprintDto? OriginalFingerprint, FingerprintDto? ExpectedPostFingerprint,
        FingerprintDto? CurrentFingerprint, long? CurrentRecordId,
        string OriginalBackupArtifactIdentity, bool PreserveCurrentContent,
        string? SourceOperationId);
    private sealed record OperationDto(
        string OperationId, RecoveryOperationKind Kind, RecoveryOperationPhase Phase,
        string LogicalRecordId, long OriginalRecordId, long? CurrentRecordId,
        long? ProposedRecoveredRecordId, string? Format,
        string? OriginalBackupArtifactIdentity, FingerprintDto? OriginalBackupFingerprint,
        FingerprintDto? ExpectedCurrentFingerprint, string[] DependencyIds,
        VerificationDto Verification, string Reason, string SourceJournalEvidence,
        RecoveryRiskLevel Risk, string[] PreservationDependencyIds,
        string RequiredCapability);
    private sealed record VerificationDto(
        string Code, string LogicalRecordId, string Description,
        long? ExpectedCurrentRecordId, string? Format,
        FingerprintDto? ExpectedFingerprint, bool ExpectedPresent);
    private sealed record ExpectedStateDto(
        ExpectedRecoveredRecordDto[] Records, PreservedDto[] PreservedContent,
        AbsentFormatDto[] ExpectedAbsentFormats, long[] ExpectedAbsentRecords,
        string UnrelatedStateFingerprint);
    private sealed record ExpectedRecoveredRecordDto(
        string LogicalRecordId, ExpectedRecordDto OriginalState,
        long? ExpectedCurrentRecordId, string[] SupportedMetadataFields);
    private sealed record ExpectedRecordDto(
        long RecordId, string Title, string AuthorSort, AuthorDto[] Authors,
        IdentifierDto[] Identifiers, string? Publisher, DateTimeOffset? PublicationDate,
        string? Series, decimal? SeriesIndex, string[] Languages, bool HasCover,
        string RelativeDirectory, ExpectedFormatDto[] Formats);
    private sealed record AuthorDto(long Id, string Name, string SortName);
    private sealed record IdentifierDto(string Type, string Value);
    private sealed record ExpectedFormatDto(
        long RecordId, string Format, string StoredFileName, string RelativePath,
        FormatFileStatus Status, FingerprintDto Fingerprint, long Length,
        DateTimeOffset CreationTimeUtc, DateTimeOffset LastWriteTimeUtc, int Attributes);
    private sealed record PreservedDto(
        string LogicalRecordId, long CurrentRecordId, string Format,
        FingerprintDto Fingerprint, string PreservationReason);
    private sealed record AbsentFormatDto(long RecordId, string Format);
    private sealed record FingerprintDto(long SizeInBytes, string Sha256);
    private sealed record IssueDto(
        string Code, RecoveryIssueSeverity Severity, string Subject, string Explanation,
        string? LogicalRecordId, long? CurrentRecordId, string? Format,
        Dictionary<string, string> Evidence);
    private sealed record ValidationDto(DateTimeOffset ValidatedAtUtc, IssueDto[] Issues);
    private sealed record ApprovalDto(
        string RecoveryPlanId, DateTimeOffset ApprovedAtUtc, int ApprovedRevision, string ContentDigest,
        string SourceExecutionId, string CurrentLibraryUuid,
        string CanonicalRootIdentityDigest, string CurrentStateFingerprint,
        string CapabilityProfile, string[] AcknowledgedWarningCodes);
    private sealed record RevocationDto(
        DateTimeOffset RevokedAtUtc, string Reason, int? PriorApprovalRevision,
        string ContentDigest);
    private sealed record CompletionDto(
        string RecoveryExecutionId, DateTimeOffset CompletedAtUtc,
        string RecoveryJournalFinalHash, bool ChangedRecordIds,
        bool PreservedUnexpectedContent);
    private sealed record LifecycleDto(
        int Revision, RecoveryPlanState FromState, RecoveryPlanState ToState,
        DateTimeOffset ChangedAtUtc, string Reason);
}
