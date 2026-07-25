using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Recoveries;

public enum RecoverySourceArtifactKind
{
    CleanupPlan,
    RecoveryPlan,
    ExecutionJournal,
    ExecutionSummary,
    OriginalBackupManifest,
    OriginalRawFormat,
    OriginalMetadataOpf,
    OriginalCover,
    ToolIdentity,
    ApplicationIdentity,
    ManagedState,
    OtherManifestEntry,
}

public sealed record VerifiedRecoverySourceArtifact(
    string RelativePath,
    string PhysicalIdentity,
    RecoverySourceArtifactKind Kind,
    long SizeInBytes,
    Sha256Digest Sha256,
    CalibreBookId? RecordId = null,
    string? Format = null);

public sealed record VerifiedExecutionJournalProjection(
    string SchemaVersion,
    CleanupExecutionId ExecutionId,
    CleanupPlanId PlanId,
    CleanupPlanContentDigest PlanContentDigest,
    string LibraryUuid,
    string ApplicationVersion,
    Sha256Digest FileDigest,
    string FinalEntryHash,
    Sha256Digest? TerminalSummaryDigest,
    CleanupExecutionState LastState,
    CleanupExecutionDisposition LastDisposition,
    bool MutationStarted,
    bool OriginalBackupVerifiedBeforeMutation,
    IReadOnlyList<SourceOperationProjection> Operations,
    IReadOnlyList<string> DurableEventKinds,
    bool HasKnownDurableState);

public sealed record RecoverySourceInspection(
    string BundleIdentity,
    CleanupPlan? CleanupPlan,
    VerifiedBackupManifest? OriginalBackupManifest,
    VerifiedExecutionJournalProjection? Journal,
    ExecutionHistoryEntry? TerminalSummary,
    ExecutionToolIdentity? SourceToolIdentity,
    string? SourceApplicationVersion,
    Sha256Digest? OriginalManifestFileDigest,
    IReadOnlyList<VerifiedRecoverySourceArtifact> Artifacts,
    IReadOnlyList<RecoveryIssue> Issues)
{
    public bool IsVerified => CleanupPlan is not null && OriginalBackupManifest is not null
        && Journal is not null && SourceToolIdentity is not null
        && OriginalManifestFileDigest is not null
        && Issues.All(value => value.Severity != RecoveryIssueSeverity.Blocking);

    public VerifiedRecoverySourceArtifact? FindOriginalFormat(CalibreBookId recordId, string format) =>
        Artifacts.SingleOrDefault(value => value.Kind == RecoverySourceArtifactKind.OriginalRawFormat
            && value.RecordId == recordId
            && string.Equals(value.Format, format, StringComparison.OrdinalIgnoreCase));
}

public sealed record RecoveryCoverEvidence(
    CalibreBookId RecordId,
    bool HasCover,
    FormatFileFingerprint? Fingerprint,
    string? ReadOnlyArtifactIdentity);

public sealed record RecoveryCurrentStateSnapshot(
    LibrarySnapshot Snapshot,
    IReadOnlyList<RecoveryCoverEvidence> Covers,
    Sha256Digest FullFingerprint,
    Sha256Digest AffectedFingerprint,
    Sha256Digest UnrelatedFingerprint);

public sealed record RecoveryCurrentStateScanResult(
    RecoveryCurrentStateSnapshot? CurrentState,
    IReadOnlyList<RecoveryIssue> Issues)
{
    public bool IsSuccess => CurrentState is not null
        && Issues.All(value => value.Severity != RecoveryIssueSeverity.Blocking);
}

public enum RecoveryCapability
{
    ExportCurrentRecord,
    CreateEmptyRecord,
    FindCreatedRecord,
    AddBackedUpFormat,
    ReplaceExistingFormat,
    RestoreTitle,
    RestoreAuthors,
    RestoreAuthorSort,
    RestorePublisher,
    RestorePublicationDate,
    RestoreLanguages,
    RestoreIdentifiers,
    RestoreSeries,
    RestoreSeriesIndex,
    RestoreCover,
    RemoveCleanupAddedFormat,
    RemoveCleanupCreatedRecord,
    VerifyRestoredContent,
}

public sealed record RecoveryCapabilityStatus(
    RecoveryCapability Capability,
    bool Documented,
    bool ClosedMappingTested,
    bool RealCalibreQualified,
    bool Enabled,
    string Explanation)
{
    public bool IsDispatchable => Documented && ClosedMappingTested && RealCalibreQualified && Enabled;
}

public sealed record RecoveryCapabilityProfile
{
    public RecoveryCapabilityProfile(
        string profileIdentity,
        ExecutionToolIdentity toolIdentity,
        IEnumerable<RecoveryCapabilityStatus> capabilities)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileIdentity);
        ProfileIdentity = profileIdentity.Trim();
        ToolIdentity = toolIdentity ?? throw new ArgumentNullException(nameof(toolIdentity));
        RecoveryCapabilityStatus[] ordered = capabilities.OrderBy(value => value.Capability).ToArray();
        if (ordered.Select(value => value.Capability).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Recovery capabilities must be unique.", nameof(capabilities));
        Capabilities = Array.AsReadOnly(ordered);
    }

    public string ProfileIdentity { get; }
    public ExecutionToolIdentity ToolIdentity { get; }
    public IReadOnlyList<RecoveryCapabilityStatus> Capabilities { get; }
    public bool Supports(RecoveryCapability capability) =>
        Capabilities.SingleOrDefault(value => value.Capability == capability)?.IsDispatchable == true;
}

public sealed record RecoveryPlanStoreResult(
    RecoveryPlan? Plan,
    string? ArtifactIdentity,
    IReadOnlyList<RecoveryIssue> Issues)
{
    public bool IsSuccess => Plan is not null && ArtifactIdentity is not null
        && Issues.All(value => value.Severity != RecoveryIssueSeverity.Blocking);
}

public sealed record RecoveryBackupDestinationValidation(
    string? CanonicalDestinationIdentity,
    long AvailableBytes,
    IReadOnlyList<RecoveryIssue> Issues)
{
    public bool IsValid => CanonicalDestinationIdentity is not null
        && Issues.All(value => value.Severity != RecoveryIssueSeverity.Blocking);
}

public sealed record RecoveryCurrentStateBackupArtifact(
    string RelativePath,
    RecoverySourceArtifactKind Kind,
    long SizeInBytes,
    Sha256Digest Sha256,
    LogicalRecoveryRecordId? LogicalRecordId = null,
    CalibreBookId? CurrentRecordId = null,
    string? Format = null,
    string? PreservationRole = null);

public sealed record RecoveryCurrentStateBackup(
    RecoveryExecutionId RecoveryExecutionId,
    RecoveryPlanId RecoveryPlanId,
    RecoveryPlanContentDigest RecoveryPlanContentDigest,
    string LibraryUuid,
    string BundleIdentity,
    string ManifestIdentity,
    Sha256Digest ManifestFileDigest,
    Sha256Digest ManifestInternalDigest,
    Sha256Digest SourceStateFingerprint,
    IReadOnlyList<RecoveryCurrentStateBackupArtifact> Artifacts,
    IReadOnlyDictionary<(CalibreBookId RecordId, string Format), string> RawFormatPhysicalIdentities,
    Sha256Digest? CanonicalRootIdentityDigest = null,
    Sha256Digest? SourceJournalFileDigest = null,
    Sha256Digest? OriginalManifestFileDigest = null);

public sealed record CreateRecoveryCurrentStateBackupRequest(
    RecoveryExecutionId RecoveryExecutionId,
    RecoveryPlan Plan,
    RecoverySourceInspection Source,
    RecoveryCurrentStateSnapshot CurrentState,
    string LibraryRoot,
    CalibreToolDescriptor Tool,
    string CanonicalDestinationIdentity,
    string BundleIdentity,
    string ApplicationVersion,
    DateTimeOffset CreatedAtUtc,
    RecoveryExecutionConfirmation Confirmation);

public sealed record RecoveryCurrentStateBackupResult(
    RecoveryCurrentStateBackup? Backup,
    IReadOnlyList<RecoveryIssue> Issues)
{
    public bool IsSuccess => Backup is not null
        && Issues.All(value => value.Severity != RecoveryIssueSeverity.Blocking);
}

public enum RecoveryCalibreMetadataField
{
    Title,
    Authors,
    AuthorSort,
    Publisher,
    PublicationDate,
    Languages,
    Identifiers,
    Series,
    SeriesIndex,
}

public sealed record RecoveryCalibreCommandResult(
    string CommandKind,
    bool Started,
    int? ExitCode,
    IReadOnlyList<string> SanitizedArguments,
    string SanitizedStandardOutput,
    string SanitizedStandardError,
    TimeSpan Duration,
    CalibreBookId? CreatedRecordId = null,
    string? FailureCode = null)
{
    public bool IsTransportSuccess => Started && ExitCode == 0 && FailureCode is null;
}

public sealed record CreateRecoveryRecordCommand(
    CalibreToolDescriptor Tool,
    RecoveryCapabilityProfile Profile,
    string LibraryRoot,
    string Title,
    IReadOnlyList<string> Authors,
    string AuthorSort);

public sealed record SetRecoveryMetadataFieldCommand(
    CalibreToolDescriptor Tool,
    RecoveryCapabilityProfile Profile,
    string LibraryRoot,
    CalibreBookId RecordId,
    RecoveryCalibreMetadataField Field,
    IReadOnlyList<string> Values);

public sealed record RestoreRecoveryFormatCommand(
    CalibreToolDescriptor Tool,
    RecoveryCapabilityProfile Profile,
    string LibraryRoot,
    CalibreBookId RecordId,
    string CanonicalFormat,
    string VerifiedOriginalBackupPhysicalIdentity,
    FormatFileFingerprint ExpectedFingerprint,
    bool ReplacesExisting);

public sealed record RemoveRecoveryFormatCommand(
    CalibreToolDescriptor Tool,
    RecoveryCapabilityProfile Profile,
    string LibraryRoot,
    CalibreBookId RecordId,
    string CanonicalFormat,
    FormatFileFingerprint ExpectedCurrentFingerprint);

public sealed record RemoveRecoveryRecordCommand(
    CalibreToolDescriptor Tool,
    RecoveryCapabilityProfile Profile,
    string LibraryRoot,
    CalibreBookId RecordId);

public enum RecoveryProgressPhase
{
    InspectingSource,
    Eligibility,
    Reconciliation,
    Planning,
    AcquiringLease,
    Preflight,
    CurrentStateBackup,
    Constructive,
    IntermediateVerification,
    DestructiveGate,
    Destructive,
    FinalVerification,
    Completed,
    Stopped,
}

public sealed record RecoveryProgress(
    RecoveryProgressPhase Phase,
    string Message,
    int CompletedOperations,
    int TotalOperations,
    bool MutationStarted,
    bool DestructiveRecoveryStarted,
    RecoveryOperationId? OperationId = null);

public sealed record RecoveryJournalEvent(
    string Kind,
    RecoveryExecutionState State,
    DateTimeOffset OccurredAtUtc,
    string Message,
    RecoveryOperationId? OperationId = null,
    string? MappedCommand = null,
    IReadOnlyList<string>? SanitizedArguments = null,
    int? ExitCode = null,
    string? SanitizedStandardOutput = null,
    string? SanitizedStandardError = null,
    string? FailureCode = null,
    bool MutationStarted = false,
    bool DestructiveRecoveryStarted = false,
    IReadOnlyList<RecoveryIssue>? Issues = null,
    RecoveryRecordIdMapping? RecordIdMapping = null,
    string? CanonicalRootIdentityDigest = null,
    string? SourceJournalFileDigest = null,
    string? OriginalManifestFileDigest = null,
    string? CurrentStateManifestFileDigest = null,
    string? CurrentStateManifestInternalDigest = null,
    string? DestructiveOperationDigest = null);

public sealed record RecoveryJournalCreateRequest(
    RecoveryExecutionId RecoveryExecutionId,
    RecoveryPlan Plan,
    RecoverySourceInspection Source,
    string BundleIdentity,
    string ApplicationVersion,
    DateTimeOffset CreatedAtUtc);

public sealed record RecoveryJournalReconciliationResult(
    bool ManualInterventionRequired,
    IReadOnlyList<RecoveryIssue> Issues);

public sealed record RecoveryHistoryEntry(
    RecoveryExecutionId RecoveryExecutionId,
    RecoveryPlanId RecoveryPlanId,
    RecoveryPlanContentDigest RecoveryPlanContentDigest,
    CleanupExecutionId SourceExecutionId,
    string LibraryUuid,
    RecoveryExecutionState State,
    RecoveryFailureClassification FailureClassification,
    string BundleIdentity,
    string JournalIdentity,
    string? CurrentStateManifestDigest,
    DateTimeOffset FinishedAtUtc,
    bool MutationStarted,
    bool DestructiveRecoveryStarted,
    bool ChangedRecordIds,
    bool PreservedUnexpectedContent,
    string? CurrentStateManifestFileDigest = null,
    string? CanonicalRootIdentityDigest = null,
    string? SourceJournalFileDigest = null,
    string? OriginalManifestFileDigest = null);

public sealed record RecoveryResolutionEntry(
    CleanupExecutionId SourceExecutionId,
    RecoveryPlanId RecoveryPlanId,
    RecoveryExecutionId RecoveryExecutionId,
    string LibraryUuid,
    string RecoveryJournalIdentity,
    string RecoveryJournalFinalHash,
    DateTimeOffset ResolvedAtUtc,
    RecoveryExecutionState State,
    string? CurrentStateManifestFileDigest = null,
    string? CurrentStateManifestInternalDigest = null,
    string? CanonicalRootIdentityDigest = null,
    string? SourceJournalFileDigest = null,
    string? OriginalManifestFileDigest = null);

public sealed record DestructiveRecoveryConfirmationRequest(
    RecoveryExecutionId RecoveryExecutionId,
    RecoveryPlanId RecoveryPlanId,
    RecoveryPlanContentDigest RecoveryPlanContentDigest,
    Sha256Digest DestructiveOperationDigest,
    IReadOnlyList<RecoveryOperation> DestructiveOperations,
    Sha256Digest CurrentStateBackupManifestFileDigest,
    Sha256Digest CurrentStateBackupManifestInternalDigest);

public sealed record InspectRecoverySourceRequest(string SourceBundle);

public sealed record EvaluateRecoveryEligibilityRequest(
    RecoverySourceInspection Source,
    RecoveryCurrentStateSnapshot CurrentState,
    string CanonicalRootIdentity,
    RecoveryCapabilityProfile CapabilityProfile,
    bool ConflictingLeaseOrRecovery);

public sealed record GenerateRecoveryPlanRequest(
    RecoverySourceInspection Source,
    CurrentStateReconciliation Reconciliation,
    string CanonicalLibraryRootIdentity,
    RecoveryCapabilityProfile CapabilityProfile);

public sealed record PrepareRecoveryExecutionRequest(
    RecoveryPlan Plan,
    RecoverySourceInspection Source,
    string LibraryRoot,
    string RecoveryBackupDestination,
    CalibreToolDescriptor Tool,
    RecoveryCapabilityProfile CapabilityProfile);

public sealed record RecoveryExecutionPreparation(
    RecoveryPlan Plan,
    RecoverySourceInspection Source,
    RecoveryCurrentStateSnapshot? CurrentState,
    string? CanonicalLibraryRootIdentity,
    string? CanonicalBackupDestinationIdentity,
    CalibreToolDescriptor? Tool,
    RecoveryCapabilityProfile? CapabilityProfile,
    IReadOnlyList<RecoveryIssue> Issues)
{
    public bool IsReady => Plan.State == RecoveryPlanState.Approved
        && CurrentState is not null && CanonicalLibraryRootIdentity is not null
        && CanonicalBackupDestinationIdentity is not null && Tool is not null
        && CapabilityProfile is not null
        && Issues.All(value => value.Severity != RecoveryIssueSeverity.Blocking);
}

public sealed record RecoveryExecutionConfirmation(
    RecoveryPlanId PlanId,
    RecoveryPlanArtifactRevision PlanRevision,
    RecoveryPlanContentDigest PlanContentDigest,
    CleanupExecutionId SourceExecutionId,
    string LibraryUuid,
    Sha256Digest CanonicalRootIdentityDigest,
    Sha256Digest CurrentStateFingerprint,
    string CapabilityProfile,
    string CurrentStateBackupDestinationIdentity,
    Sha256Digest DestructiveOperationDigest,
    DateTimeOffset ConfirmedAtUtc,
    bool OtherMutatorsClosed,
    bool SafeBoundaryCancellationUnderstood);

public sealed record ExecuteRecoveryPlanRequest(
    RecoveryPlan Plan,
    RecoverySourceInspection Source,
    string LibraryRoot,
    string RecoveryBackupDestination,
    CalibreToolDescriptor Tool,
    RecoveryCapabilityProfile CapabilityProfile,
    RecoveryExecutionConfirmation Confirmation,
    string ApplicationVersion);

public sealed record RecoveryExecutionResult(
    RecoveryExecutionId RecoveryExecutionId,
    RecoveryExecutionState State,
    RecoveryFailureClassification FailureClassification,
    IReadOnlyList<RecoveryIssue> Issues,
    IReadOnlyList<RecoveryRecordIdMapping> RecordIdMappings,
    string? BundleIdentity,
    string? JournalIdentity,
    string? CurrentStateBackupManifestDigest,
    bool MutationStarted,
    bool DestructiveRecoveryStarted,
    bool SemanticPreStateRestored,
    bool PreservedUnexpectedContent)
{
    public bool IsRecovered => State == RecoveryExecutionState.Recovered && SemanticPreStateRestored;
}
