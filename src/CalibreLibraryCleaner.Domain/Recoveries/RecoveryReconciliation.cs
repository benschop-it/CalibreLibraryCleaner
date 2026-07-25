using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Recoveries;

public enum RecoveryReconciliationClassification
{
    UnchangedFromPreState,
    MatchesExpectedPostState,
    CompletedAndMatchesJournalPostState,
    CompletedButCurrentStateDiffers,
    NotCompletedButCurrentStateChanged,
    CommandOutcomeUncertain,
    Missing,
    NewlyCreatedByExecution,
    ReplacedByExecution,
    AlreadyRestored,
    IndependentlyRecreated,
    IndependentlyModifiedAfterExecution,
    UnexpectedUniqueContent,
    DifferentCurrentRecordId,
    Ambiguous,
}

public enum DurableSourceOperationState
{
    NotStarted,
    SatisfiedNoOp,
    DurablyCompleted,
    FailedBeforeCompletion,
    CommandOutcomeUncertain,
    Contradictory,
}

public sealed record SourceOperationProjection
{
    public SourceOperationProjection(
        ExecutionOperationId operationId,
        ExecutionOperationKind kind,
        DurableSourceOperationState state,
        CalibreBookId targetRecordId,
        CalibreBookId? sourceRecordId,
        string? format,
        IReadOnlyList<ExecutionOperationId>? dependencyIds = null)
    {
        OperationId = operationId ?? throw new ArgumentNullException(nameof(operationId));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        Kind = kind;
        State = state;
        TargetRecordId = targetRecordId;
        SourceRecordId = sourceRecordId;
        Format = string.IsNullOrWhiteSpace(format) ? null : format.Trim().ToUpperInvariant();
        DependencyIds = Array.AsReadOnly((dependencyIds ?? []).Distinct()
            .OrderBy(value => value.Value, StringComparer.Ordinal).ToArray());
    }

    public ExecutionOperationId OperationId { get; }
    public ExecutionOperationKind Kind { get; }
    public DurableSourceOperationState State { get; }
    public CalibreBookId TargetRecordId { get; }
    public CalibreBookId? SourceRecordId { get; }
    public string? Format { get; }
    public IReadOnlyList<ExecutionOperationId> DependencyIds { get; }
}

public sealed record ReconciledFormat
{
    public ReconciledFormat(
        LogicalRecoveryRecordId logicalRecordId,
        string format,
        RecoveryReconciliationClassification classification,
        FormatFileFingerprint? originalFingerprint,
        FormatFileFingerprint? expectedPostFingerprint,
        FormatFileFingerprint? currentFingerprint,
        CalibreBookId? currentRecordId,
        string originalBackupArtifactIdentity,
        bool preserveCurrentContent,
        ExecutionOperationId? sourceOperationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        if (!Enum.IsDefined(classification)) throw new ArgumentOutOfRangeException(nameof(classification));
        ArgumentException.ThrowIfNullOrWhiteSpace(originalBackupArtifactIdentity);
        LogicalRecordId = logicalRecordId ?? throw new ArgumentNullException(nameof(logicalRecordId));
        Format = format.Trim().ToUpperInvariant();
        Classification = classification;
        OriginalFingerprint = originalFingerprint;
        ExpectedPostFingerprint = expectedPostFingerprint;
        CurrentFingerprint = currentFingerprint;
        CurrentRecordId = currentRecordId;
        OriginalBackupArtifactIdentity = originalBackupArtifactIdentity.Trim();
        PreserveCurrentContent = preserveCurrentContent;
        SourceOperationId = sourceOperationId;
    }

    public LogicalRecoveryRecordId LogicalRecordId { get; }
    public string Format { get; }
    public RecoveryReconciliationClassification Classification { get; }
    public FormatFileFingerprint? OriginalFingerprint { get; }
    public FormatFileFingerprint? ExpectedPostFingerprint { get; }
    public FormatFileFingerprint? CurrentFingerprint { get; }
    public CalibreBookId? CurrentRecordId { get; }
    public string OriginalBackupArtifactIdentity { get; }
    public bool PreserveCurrentContent { get; }
    public ExecutionOperationId? SourceOperationId { get; }
}

public sealed record ReconciledRecoveryRecord
{
    public ReconciledRecoveryRecord(
        RecoveryRecordIdentity identity,
        IEnumerable<RecoveryReconciliationClassification> classifications,
        IEnumerable<ReconciledFormat> formats,
        IEnumerable<CalibreBookId>? ambiguousCandidateIds = null)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        RecoveryReconciliationClassification[] orderedClassifications = classifications.Distinct().Order().ToArray();
        if (orderedClassifications.Length == 0)
            throw new ArgumentException("A reconciled record requires a classification.", nameof(classifications));
        ReconciledFormat[] orderedFormats = formats.OrderBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.OriginalBackupArtifactIdentity, StringComparer.Ordinal).ToArray();
        if (orderedFormats.Any(value => value.LogicalRecordId != identity.LogicalRecordId)
            || orderedFormats.Select(value => value.Format).Distinct(StringComparer.Ordinal).Count() != orderedFormats.Length)
            throw new ArgumentException("Reconciled formats must be unique and owned by their logical record.", nameof(formats));
        Classifications = Array.AsReadOnly(orderedClassifications);
        Formats = Array.AsReadOnly(orderedFormats);
        AmbiguousCandidateIds = Array.AsReadOnly((ambiguousCandidateIds ?? []).Distinct().OrderBy(value => value.Value).ToArray());
    }

    public RecoveryRecordIdentity Identity { get; }
    public IReadOnlyList<RecoveryReconciliationClassification> Classifications { get; }
    public IReadOnlyList<ReconciledFormat> Formats { get; }
    public IReadOnlyList<CalibreBookId> AmbiguousCandidateIds { get; }
}

public sealed record CurrentStateReconciliation
{
    public CurrentStateReconciliation(
        ReconciliationModelVersion version,
        string sourceExecutionIdentity,
        string currentLibraryUuid,
        int currentLibrarySchemaVersion,
        Sha256Digest fullStateFingerprint,
        Sha256Digest affectedStateFingerprint,
        Sha256Digest unrelatedStateFingerprint,
        IEnumerable<SourceOperationProjection> sourceOperations,
        IEnumerable<ReconciledRecoveryRecord> records,
        IEnumerable<RecoveryIssue> issues,
        Sha256Digest digest)
    {
        Version = version ?? throw new ArgumentNullException(nameof(version));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceExecutionIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentLibraryUuid);
        if (!Guid.TryParse(currentLibraryUuid, out _))
            throw new ArgumentException("The reconciled library UUID is invalid.", nameof(currentLibraryUuid));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(currentLibrarySchemaVersion);
        SourceOperationProjection[] orderedOperations = sourceOperations.OrderBy(value => value.OperationId.Value, StringComparer.Ordinal).ToArray();
        if (orderedOperations.Select(value => value.OperationId).Distinct().Count() != orderedOperations.Length)
            throw new ArgumentException("Source operation projections must be unique.", nameof(sourceOperations));
        ReconciledRecoveryRecord[] orderedRecords = records.OrderBy(value => value.Identity.LogicalRecordId.Value, StringComparer.Ordinal).ToArray();
        if (orderedRecords.Select(value => value.Identity.LogicalRecordId).Distinct().Count() != orderedRecords.Length)
            throw new ArgumentException("Reconciled logical records must be unique.", nameof(records));
        SourceExecutionIdentity = sourceExecutionIdentity.Trim();
        CurrentLibraryUuid = currentLibraryUuid.Trim();
        CurrentLibrarySchemaVersion = currentLibrarySchemaVersion;
        FullStateFingerprint = fullStateFingerprint;
        AffectedStateFingerprint = affectedStateFingerprint;
        UnrelatedStateFingerprint = unrelatedStateFingerprint;
        SourceOperations = Array.AsReadOnly(orderedOperations);
        Records = Array.AsReadOnly(orderedRecords);
        Issues = new RecoveryEligibilityResult(issues, DateTimeOffset.UnixEpoch).Issues;
        Digest = digest;
        if (RecoveryReconciliationDigestPolicy.Compute(this) != digest)
            throw new ArgumentException("The reconciliation digest is invalid.", nameof(digest));
    }

    public ReconciliationModelVersion Version { get; }
    public string SourceExecutionIdentity { get; }
    public string CurrentLibraryUuid { get; }
    public int CurrentLibrarySchemaVersion { get; }
    public Sha256Digest FullStateFingerprint { get; }
    public Sha256Digest AffectedStateFingerprint { get; }
    public Sha256Digest UnrelatedStateFingerprint { get; }
    public IReadOnlyList<SourceOperationProjection> SourceOperations { get; }
    public IReadOnlyList<ReconciledRecoveryRecord> Records { get; }
    public IReadOnlyList<RecoveryIssue> Issues { get; }
    public Sha256Digest Digest { get; init; }

    public static CurrentStateReconciliation Create(
        string sourceExecutionIdentity,
        string currentLibraryUuid,
        int currentLibrarySchemaVersion,
        Sha256Digest fullStateFingerprint,
        Sha256Digest affectedStateFingerprint,
        Sha256Digest unrelatedStateFingerprint,
        IEnumerable<SourceOperationProjection> sourceOperations,
        IEnumerable<ReconciledRecoveryRecord> records,
        IEnumerable<RecoveryIssue> issues)
    {
        SourceOperationProjection[] operationValues = sourceOperations.ToArray();
        ReconciledRecoveryRecord[] recordValues = records.ToArray();
        RecoveryIssue[] issueValues = issues.ToArray();
        CurrentStateReconciliation unsigned = new(ReconciliationModelVersion.V1, sourceExecutionIdentity,
            currentLibraryUuid, currentLibrarySchemaVersion, fullStateFingerprint, affectedStateFingerprint,
            unrelatedStateFingerprint, operationValues, recordValues, issueValues,
            new Sha256Digest(new string('0', 64)), skipValidation: true);
        return new(ReconciliationModelVersion.V1, sourceExecutionIdentity, currentLibraryUuid,
            currentLibrarySchemaVersion, fullStateFingerprint, affectedStateFingerprint,
            unrelatedStateFingerprint, operationValues, recordValues, issueValues,
            RecoveryReconciliationDigestPolicy.Compute(unsigned));
    }

    private CurrentStateReconciliation(
        ReconciliationModelVersion version,
        string sourceExecutionIdentity,
        string currentLibraryUuid,
        int currentLibrarySchemaVersion,
        Sha256Digest fullStateFingerprint,
        Sha256Digest affectedStateFingerprint,
        Sha256Digest unrelatedStateFingerprint,
        IEnumerable<SourceOperationProjection> sourceOperations,
        IEnumerable<ReconciledRecoveryRecord> records,
        IEnumerable<RecoveryIssue> issues,
        Sha256Digest digest,
        bool skipValidation)
    {
        _ = skipValidation;
        Version = version;
        SourceExecutionIdentity = sourceExecutionIdentity;
        CurrentLibraryUuid = currentLibraryUuid;
        CurrentLibrarySchemaVersion = currentLibrarySchemaVersion;
        FullStateFingerprint = fullStateFingerprint;
        AffectedStateFingerprint = affectedStateFingerprint;
        UnrelatedStateFingerprint = unrelatedStateFingerprint;
        SourceOperations = Array.AsReadOnly(sourceOperations.OrderBy(value => value.OperationId.Value, StringComparer.Ordinal).ToArray());
        Records = Array.AsReadOnly(records.OrderBy(value => value.Identity.LogicalRecordId.Value, StringComparer.Ordinal).ToArray());
        Issues = new RecoveryEligibilityResult(issues, DateTimeOffset.UnixEpoch).Issues;
        Digest = digest;
    }
}

public static class RecoveryReconciliationDigestPolicy
{
    public static Sha256Digest Compute(CurrentStateReconciliation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder canonical = new();
        Add(canonical, value.Version.Value);
        Add(canonical, value.SourceExecutionIdentity);
        Add(canonical, value.CurrentLibraryUuid);
        Add(canonical, value.CurrentLibrarySchemaVersion.ToString(CultureInfo.InvariantCulture));
        Add(canonical, value.FullStateFingerprint.Value);
        Add(canonical, value.AffectedStateFingerprint.Value);
        Add(canonical, value.UnrelatedStateFingerprint.Value);
        foreach (SourceOperationProjection operation in value.SourceOperations)
        {
            Add(canonical, operation.OperationId.Value);
            Add(canonical, operation.Kind.ToString());
            Add(canonical, operation.State.ToString());
            Add(canonical, operation.TargetRecordId.Value.ToString(CultureInfo.InvariantCulture));
            Add(canonical, operation.SourceRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, operation.Format ?? string.Empty);
            foreach (ExecutionOperationId dependency in operation.DependencyIds) Add(canonical, dependency.Value);
            Add(canonical, "|");
        }
        foreach (ReconciledRecoveryRecord record in value.Records)
        {
            RecoveryRecordIdentity identity = record.Identity;
            Add(canonical, identity.LogicalRecordId.Value);
            Add(canonical, identity.OriginalRecordId.Value.ToString(CultureInfo.InvariantCulture));
            Add(canonical, identity.CurrentRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, identity.RecoveredRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, identity.MetadataFingerprint);
            Add(canonical, identity.CurrentMetadataFingerprint ?? string.Empty);
            foreach (string identifier in identity.Identifiers) Add(canonical, identifier);
            foreach (RecoveryFormatIdentity formatIdentity in identity.BackedUpFormats)
            {
                Add(canonical, formatIdentity.Format);
                Add(canonical, formatIdentity.Fingerprint.SizeInBytes.ToString(CultureInfo.InvariantCulture));
                Add(canonical, formatIdentity.Fingerprint.Sha256.Value);
                Add(canonical, formatIdentity.BackupArtifactIdentity);
            }
            Add(canonical, identity.OriginalBackupArtifactIdentity);
            Add(canonical, identity.PreservedSeparateRecordId?.Value.ToString(CultureInfo.InvariantCulture)
                ?? string.Empty);
            foreach (RecoveryReconciliationClassification classification in record.Classifications) Add(canonical, classification.ToString());
            foreach (ReconciledFormat format in record.Formats)
            {
                Add(canonical, format.Format);
                Add(canonical, format.Classification.ToString());
                Add(canonical, format.OriginalFingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                Add(canonical, format.OriginalFingerprint?.Sha256.Value ?? string.Empty);
                Add(canonical, format.ExpectedPostFingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                Add(canonical, format.ExpectedPostFingerprint?.Sha256.Value ?? string.Empty);
                Add(canonical, format.CurrentFingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                Add(canonical, format.CurrentFingerprint?.Sha256.Value ?? string.Empty);
                Add(canonical, format.CurrentRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                Add(canonical, format.PreserveCurrentContent ? "1" : "0");
                Add(canonical, format.OriginalBackupArtifactIdentity);
                Add(canonical, format.SourceOperationId?.Value ?? string.Empty);
            }
            foreach (CalibreBookId candidate in record.AmbiguousCandidateIds)
                Add(canonical, candidate.Value.ToString(CultureInfo.InvariantCulture));
            Add(canonical, "|");
        }
        foreach (RecoveryIssue issue in value.Issues)
        {
            Add(canonical, issue.Severity.ToString());
            Add(canonical, issue.Code);
            Add(canonical, issue.Subject);
            Add(canonical, issue.Explanation);
            Add(canonical, issue.LogicalRecordId?.Value ?? string.Empty);
            Add(canonical, issue.CurrentRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, issue.Format ?? string.Empty);
            foreach ((string key, string evidence) in issue.Evidence)
            {
                Add(canonical, key);
                Add(canonical, evidence);
            }
        }
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant());
    }

    private static void Add(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
}
