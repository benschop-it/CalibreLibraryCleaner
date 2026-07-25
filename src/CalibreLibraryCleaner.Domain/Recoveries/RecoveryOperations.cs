using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Domain.Recoveries;

public sealed record RecoveryOperationId
{
    public RecoveryOperationId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256) throw new ArgumentException("A recovery operation ID is too long.", nameof(value));
        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public enum RecoveryOperationKind
{
    CreateRecoveredRecord,
    RestoreMetadataFromBackup,
    RestoreCoverFromBackup,
    RestoreFormatFromBackup,
    RemoveFormatAddedByExecution,
    RemoveRecordCreatedByExecution,
    PreserveUnexpectedCurrentFormat,
    CreateRecoveryCopyInsteadOfOverwrite,
    NoActionAlreadyRestored,
    ManualInterventionRequired,
}

public enum RecoveryOperationPhase
{
    Constructive,
    Destructive,
    NonMutating,
}

public enum RecoveryRiskLevel
{
    None,
    Constructive,
    Destructive,
    Manual,
}

public sealed record RecoveryVerificationExpectation
{
    public RecoveryVerificationExpectation(
        string code,
        LogicalRecoveryRecordId logicalRecordId,
        string description,
        CalibreBookId? expectedCurrentRecordId = null,
        string? format = null,
        FormatFileFingerprint? expectedFingerprint = null,
        bool expectedPresent = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (code.Length > 128 || description.Length > 1024)
            throw new ArgumentException("Recovery verification expectation text exceeds its bound.");
        Code = code.Trim();
        LogicalRecordId = logicalRecordId ?? throw new ArgumentNullException(nameof(logicalRecordId));
        Description = description.Trim();
        ExpectedCurrentRecordId = expectedCurrentRecordId;
        Format = string.IsNullOrWhiteSpace(format) ? null : format.Trim().ToUpperInvariant();
        ExpectedFingerprint = expectedFingerprint;
        ExpectedPresent = expectedPresent;
        if ((ExpectedFingerprint is not null) != (Format is not null))
            throw new ArgumentException("A format fingerprint expectation requires a canonical format.");
    }

    public string Code { get; }
    public LogicalRecoveryRecordId LogicalRecordId { get; }
    public string Description { get; }
    public CalibreBookId? ExpectedCurrentRecordId { get; }
    public string? Format { get; }
    public FormatFileFingerprint? ExpectedFingerprint { get; }
    public bool ExpectedPresent { get; }
}

public sealed record RecoveryOperation
{
    public RecoveryOperation(
        RecoveryOperationId id,
        RecoveryOperationKind kind,
        RecoveryOperationPhase phase,
        LogicalRecoveryRecordId logicalRecordId,
        CalibreBookId originalRecordId,
        CalibreBookId? currentRecordId,
        CalibreBookId? proposedRecoveredRecordId,
        string? format,
        string? originalBackupArtifactIdentity,
        FormatFileFingerprint? originalBackupFingerprint,
        FormatFileFingerprint? expectedCurrentFingerprint,
        IEnumerable<RecoveryOperationId> dependencyIds,
        RecoveryVerificationExpectation verification,
        string reason,
        string sourceJournalEvidence,
        RecoveryRiskLevel risk,
        IEnumerable<RecoveryOperationId>? preservationDependencyIds,
        string requiredCapability)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(phase)) throw new ArgumentOutOfRangeException(nameof(phase));
        if (!Enum.IsDefined(risk)) throw new ArgumentOutOfRangeException(nameof(risk));
        RecoveryOperationPhase requiredPhase = kind switch
        {
            RecoveryOperationKind.CreateRecoveredRecord
                or RecoveryOperationKind.RestoreMetadataFromBackup
                or RecoveryOperationKind.RestoreCoverFromBackup
                or RecoveryOperationKind.RestoreFormatFromBackup
                or RecoveryOperationKind.CreateRecoveryCopyInsteadOfOverwrite =>
                RecoveryOperationPhase.Constructive,
            RecoveryOperationKind.RemoveFormatAddedByExecution
                or RecoveryOperationKind.RemoveRecordCreatedByExecution =>
                RecoveryOperationPhase.Destructive,
            RecoveryOperationKind.PreserveUnexpectedCurrentFormat
                or RecoveryOperationKind.NoActionAlreadyRestored
                or RecoveryOperationKind.ManualInterventionRequired =>
                RecoveryOperationPhase.NonMutating,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        RecoveryRiskLevel requiredRisk = requiredPhase switch
        {
            RecoveryOperationPhase.Constructive => RecoveryRiskLevel.Constructive,
            RecoveryOperationPhase.Destructive => RecoveryRiskLevel.Destructive,
            RecoveryOperationPhase.NonMutating
                when kind == RecoveryOperationKind.ManualInterventionRequired =>
                RecoveryRiskLevel.Manual,
            RecoveryOperationPhase.NonMutating => RecoveryRiskLevel.None,
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };
        if (phase != requiredPhase || risk != requiredRisk)
            throw new ArgumentException(
                "The recovery operation kind, phase, and risk classification disagree.");
        LogicalRecordId = logicalRecordId ?? throw new ArgumentNullException(nameof(logicalRecordId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceJournalEvidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredCapability);
        string? canonicalFormat = string.IsNullOrWhiteSpace(format) ? null : format.Trim().ToUpperInvariant();
        bool formatOperation = kind is RecoveryOperationKind.RestoreFormatFromBackup
            or RecoveryOperationKind.RemoveFormatAddedByExecution
            or RecoveryOperationKind.PreserveUnexpectedCurrentFormat
            or RecoveryOperationKind.CreateRecoveryCopyInsteadOfOverwrite;
        if (formatOperation != (canonicalFormat is not null))
            throw new ArgumentException("Format recovery operations require a canonical format.", nameof(format));
        bool backupRequired = kind is RecoveryOperationKind.RestoreMetadataFromBackup
            or RecoveryOperationKind.RestoreCoverFromBackup
            or RecoveryOperationKind.RestoreFormatFromBackup
            or RecoveryOperationKind.CreateRecoveryCopyInsteadOfOverwrite;
        if (backupRequired && string.IsNullOrWhiteSpace(originalBackupArtifactIdentity))
            throw new ArgumentException("This recovery operation requires a verified original backup artifact.", nameof(originalBackupArtifactIdentity));
        if (kind is RecoveryOperationKind.RestoreFormatFromBackup or RecoveryOperationKind.CreateRecoveryCopyInsteadOfOverwrite
            && originalBackupFingerprint is null)
            throw new ArgumentException("A format restoration requires the original backup fingerprint.", nameof(originalBackupFingerprint));
        Kind = kind;
        Phase = phase;
        OriginalRecordId = originalRecordId;
        CurrentRecordId = currentRecordId;
        ProposedRecoveredRecordId = proposedRecoveredRecordId;
        Format = canonicalFormat;
        OriginalBackupArtifactIdentity = string.IsNullOrWhiteSpace(originalBackupArtifactIdentity)
            ? null : originalBackupArtifactIdentity.Trim();
        OriginalBackupFingerprint = originalBackupFingerprint;
        ExpectedCurrentFingerprint = expectedCurrentFingerprint;
        DependencyIds = Array.AsReadOnly(dependencyIds.Distinct()
            .OrderBy(value => value.Value, StringComparer.Ordinal).ToArray());
        Verification = verification ?? throw new ArgumentNullException(nameof(verification));
        if (verification.LogicalRecordId != logicalRecordId)
            throw new ArgumentException("The operation and verification expectation target different logical records.", nameof(verification));
        Reason = reason.Trim();
        SourceJournalEvidence = sourceJournalEvidence.Trim();
        Risk = risk;
        PreservationDependencyIds = Array.AsReadOnly((preservationDependencyIds ?? []).Distinct()
            .OrderBy(value => value.Value, StringComparer.Ordinal).ToArray());
        RequiredCapability = requiredCapability.Trim();
    }

    public RecoveryOperationId Id { get; }
    public RecoveryOperationKind Kind { get; }
    public RecoveryOperationPhase Phase { get; }
    public LogicalRecoveryRecordId LogicalRecordId { get; }
    public CalibreBookId OriginalRecordId { get; }
    public CalibreBookId? CurrentRecordId { get; }
    public CalibreBookId? ProposedRecoveredRecordId { get; }
    public string? Format { get; }
    public string? OriginalBackupArtifactIdentity { get; }
    public FormatFileFingerprint? OriginalBackupFingerprint { get; }
    public FormatFileFingerprint? ExpectedCurrentFingerprint { get; }
    public IReadOnlyList<RecoveryOperationId> DependencyIds { get; }
    public RecoveryVerificationExpectation Verification { get; }
    public string Reason { get; }
    public string SourceJournalEvidence { get; }
    public RecoveryRiskLevel Risk { get; }
    public IReadOnlyList<RecoveryOperationId> PreservationDependencyIds { get; }
    public string RequiredCapability { get; }
    public bool IsDispatchable => Kind is RecoveryOperationKind.CreateRecoveredRecord
        or RecoveryOperationKind.RestoreMetadataFromBackup
        or RecoveryOperationKind.RestoreCoverFromBackup
        or RecoveryOperationKind.RestoreFormatFromBackup
        or RecoveryOperationKind.RemoveFormatAddedByExecution
        or RecoveryOperationKind.RemoveRecordCreatedByExecution;
}

public sealed record RecoveryOperationGraph
{
    public RecoveryOperationGraph(IEnumerable<RecoveryOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        RecoveryOperation[] supplied = operations.ToArray();
        if (supplied.Select(value => value.Id).Distinct().Count() != supplied.Length)
            throw new ArgumentException("Recovery operations require unique IDs.", nameof(operations));
        Dictionary<RecoveryOperationId, RecoveryOperation> byId = supplied.ToDictionary(value => value.Id);
        foreach (RecoveryOperation operation in supplied)
        {
            if (operation.DependencyIds.Concat(operation.PreservationDependencyIds)
                .Any(dependency => dependency == operation.Id || !byId.ContainsKey(dependency)))
                throw new ArgumentException("A recovery operation dependency is missing or self-referential.", nameof(operations));
            if (operation.Phase == RecoveryOperationPhase.Constructive
                && operation.DependencyIds.Any(dependency => byId[dependency].Phase == RecoveryOperationPhase.Destructive))
                throw new ArgumentException("A constructive operation cannot depend on a destructive operation.", nameof(operations));
            if (operation.Phase == RecoveryOperationPhase.Destructive
                && !operation.DependencyIds.Any(dependency => byId[dependency].Phase != RecoveryOperationPhase.Destructive))
                throw new ArgumentException("Every destructive operation requires a verified non-destructive dependency.", nameof(operations));
        }

        List<RecoveryOperation> ordered = TopologicalSort(supplied, byId);
        if (ordered.SkipWhile(value => value.Phase != RecoveryOperationPhase.Destructive)
            .Any(value => value.Phase == RecoveryOperationPhase.Constructive))
            throw new ArgumentException("Constructive recovery must precede destructive recovery.", nameof(operations));
        Operations = Array.AsReadOnly(ordered.ToArray());
        Digest = ComputeCanonicalDigest(Operations);
    }

    public IReadOnlyList<RecoveryOperation> Operations { get; }
    public Sha256Digest Digest { get; }
    public IReadOnlyList<RecoveryOperation> ConstructiveOperations =>
        Operations.Where(value => value.Phase == RecoveryOperationPhase.Constructive).ToArray();
    public IReadOnlyList<RecoveryOperation> DestructiveOperations =>
        Operations.Where(value => value.Phase == RecoveryOperationPhase.Destructive).ToArray();
    public IReadOnlyList<RecoveryOperation> NonMutatingOperations =>
        Operations.Where(value => value.Phase == RecoveryOperationPhase.NonMutating).ToArray();

    private static List<RecoveryOperation> TopologicalSort(
        RecoveryOperation[] operations,
        IReadOnlyDictionary<RecoveryOperationId, RecoveryOperation> byId)
    {
        Dictionary<RecoveryOperationId, int> indegree = operations.ToDictionary(
            value => value.Id,
            value => value.DependencyIds.Concat(value.PreservationDependencyIds).Distinct().Count());
        List<RecoveryOperation> result = [];
        while (result.Count < operations.Length)
        {
            RecoveryOperation? next = operations.Where(value => indegree[value.Id] == 0 && !result.Contains(value))
                .OrderBy(value => value.LogicalRecordId.Value, StringComparer.Ordinal)
                .ThenBy(value => PhaseRank(value.Phase))
                .ThenBy(value => KindRank(value.Kind))
                .ThenBy(value => value.Format, StringComparer.Ordinal)
                .ThenBy(value => value.OriginalBackupArtifactIdentity, StringComparer.Ordinal)
                .ThenBy(value => value.Id.Value, StringComparer.Ordinal)
                .FirstOrDefault();
            if (next is null) throw new ArgumentException("The recovery operation graph contains a dependency cycle.", nameof(operations));
            result.Add(next);
            foreach (RecoveryOperation dependent in operations.Where(value =>
                         value.DependencyIds.Contains(next.Id) || value.PreservationDependencyIds.Contains(next.Id)))
                indegree[dependent.Id]--;
        }
        return result;
    }

    private static int PhaseRank(RecoveryOperationPhase phase) => phase switch
    {
        RecoveryOperationPhase.NonMutating => 0,
        RecoveryOperationPhase.Constructive => 1,
        RecoveryOperationPhase.Destructive => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    private static int KindRank(RecoveryOperationKind kind) => kind switch
    {
        RecoveryOperationKind.PreserveUnexpectedCurrentFormat => 0,
        RecoveryOperationKind.NoActionAlreadyRestored => 1,
        RecoveryOperationKind.CreateRecoveredRecord => 2,
        RecoveryOperationKind.RestoreMetadataFromBackup => 3,
        RecoveryOperationKind.RestoreFormatFromBackup => 4,
        RecoveryOperationKind.RestoreCoverFromBackup => 5,
        RecoveryOperationKind.CreateRecoveryCopyInsteadOfOverwrite => 6,
        RecoveryOperationKind.RemoveFormatAddedByExecution => 7,
        RecoveryOperationKind.RemoveRecordCreatedByExecution => 8,
        RecoveryOperationKind.ManualInterventionRequired => 9,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static Sha256Digest ComputeCanonicalDigest(IEnumerable<RecoveryOperation> operations)
    {
        StringBuilder canonical = new();
        Add(canonical, "cleanup-recovery-operation-graph/1.0");
        foreach (RecoveryOperation operation in operations)
        {
            Add(canonical, operation.Id.Value);
            Add(canonical, operation.Kind.ToString());
            Add(canonical, operation.Phase.ToString());
            Add(canonical, operation.LogicalRecordId.Value);
            Add(canonical, operation.OriginalRecordId.Value.ToString(CultureInfo.InvariantCulture));
            Add(canonical, operation.CurrentRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, operation.ProposedRecoveredRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, operation.Format ?? string.Empty);
            Add(canonical, operation.OriginalBackupArtifactIdentity ?? string.Empty);
            Add(canonical, operation.OriginalBackupFingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, operation.OriginalBackupFingerprint?.Sha256.Value ?? string.Empty);
            Add(canonical, operation.ExpectedCurrentFingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            Add(canonical, operation.ExpectedCurrentFingerprint?.Sha256.Value ?? string.Empty);
            Add(canonical, operation.Risk.ToString());
            Add(canonical, operation.RequiredCapability);
            foreach (RecoveryOperationId dependency in operation.DependencyIds) Add(canonical, dependency.Value);
            foreach (RecoveryOperationId dependency in operation.PreservationDependencyIds) Add(canonical, dependency.Value);
            Add(canonical, operation.Verification.Code);
            Add(canonical, operation.Verification.LogicalRecordId.Value);
            Add(canonical, operation.Verification.Description);
            Add(canonical, operation.Verification.ExpectedCurrentRecordId?.Value.ToString(CultureInfo.InvariantCulture)
                ?? string.Empty);
            Add(canonical, operation.Verification.Format ?? string.Empty);
            Add(canonical, operation.Verification.ExpectedFingerprint?.SizeInBytes.ToString(CultureInfo.InvariantCulture)
                ?? string.Empty);
            Add(canonical, operation.Verification.ExpectedFingerprint?.Sha256.Value ?? string.Empty);
            Add(canonical, operation.Verification.ExpectedPresent ? "1" : "0");
            Add(canonical, operation.Reason);
            Add(canonical, operation.SourceJournalEvidence);
            Add(canonical, "|");
        }
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant());
    }

    private static void Add(StringBuilder builder, string value) =>
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
}

public sealed record PreservedContentExpectation
{
    public PreservedContentExpectation(
        LogicalRecoveryRecordId logicalRecordId,
        CalibreBookId currentRecordId,
        string format,
        FormatFileFingerprint fingerprint,
        string preservationReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(preservationReason);
        LogicalRecordId = logicalRecordId ?? throw new ArgumentNullException(nameof(logicalRecordId));
        CurrentRecordId = currentRecordId;
        Format = format.Trim().ToUpperInvariant();
        Fingerprint = fingerprint;
        PreservationReason = preservationReason.Trim();
    }

    public LogicalRecoveryRecordId LogicalRecordId { get; }
    public CalibreBookId CurrentRecordId { get; }
    public string Format { get; }
    public FormatFileFingerprint Fingerprint { get; }
    public string PreservationReason { get; }
}

public sealed record ExpectedRecoveredRecordState(
    LogicalRecoveryRecordId LogicalRecordId,
    ExpectedRecordState OriginalState,
    CalibreBookId? ExpectedCurrentRecordId,
    IReadOnlyList<string> SupportedMetadataFields);

public sealed record ExpectedRecoveredState
{
    public ExpectedRecoveredState(
        IEnumerable<ExpectedRecoveredRecordState> records,
        IEnumerable<PreservedContentExpectation> preservedContent,
        IEnumerable<(CalibreBookId RecordId, string Format)> expectedAbsentFormats,
        IEnumerable<CalibreBookId> expectedAbsentRecords,
        Sha256Digest unrelatedStateFingerprint)
    {
        Records = Array.AsReadOnly(records.OrderBy(value => value.LogicalRecordId.Value, StringComparer.Ordinal).ToArray());
        if (Records.Select(value => value.LogicalRecordId).Distinct().Count() != Records.Count)
            throw new ArgumentException("Expected recovered logical records must be unique.", nameof(records));
        PreservedContent = Array.AsReadOnly(preservedContent
            .OrderBy(value => value.LogicalRecordId.Value, StringComparer.Ordinal)
            .ThenBy(value => value.Format, StringComparer.Ordinal).ToArray());
        ExpectedAbsentFormats = Array.AsReadOnly(expectedAbsentFormats.Distinct()
            .OrderBy(value => value.RecordId.Value).ThenBy(value => value.Format, StringComparer.Ordinal).ToArray());
        ExpectedAbsentRecords = Array.AsReadOnly(expectedAbsentRecords.Distinct().OrderBy(value => value.Value).ToArray());
        UnrelatedStateFingerprint = unrelatedStateFingerprint;
    }

    public IReadOnlyList<ExpectedRecoveredRecordState> Records { get; }
    public IReadOnlyList<PreservedContentExpectation> PreservedContent { get; }
    public IReadOnlyList<(CalibreBookId RecordId, string Format)> ExpectedAbsentFormats { get; }
    public IReadOnlyList<CalibreBookId> ExpectedAbsentRecords { get; }
    public Sha256Digest UnrelatedStateFingerprint { get; }
}
