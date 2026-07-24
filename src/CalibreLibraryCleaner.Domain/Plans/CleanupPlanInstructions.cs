using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Plans;

public enum FormatRetentionMode
{
    RetainInTarget,
    RetainFromOtherRecord,
}

public enum FormatRemovalReason
{
    ByteIdenticalAlternative,
    ReviewedNonIdenticalReplacement,
    RemovedWithSourceRecordAfterRetention,
}

public enum BackupRequirementKind
{
    RecordMetadataSnapshot,
    FormatFile,
    CoverIfPresent,
    ManagedPathAndFileState,
    CleanupPlanArtifact,
    ExecutionAudit,
}

public sealed record MetadataRetentionInstruction(
    CalibreBookId TargetRecordId,
    CalibreBookId SourceRecordId,
    CalibreBookId ExpectedMetadataStateRecordId);

public sealed record FormatRetentionInstruction
{
    private static readonly string[] RequiredPreconditions =
    [
        "Expected source and target state must still match.",
        "All required backups must be created and verified before later execution.",
    ];

    public FormatRetentionInstruction(
        string id,
        string format,
        CalibreBookId targetRecordId,
        ExpectedFormatState sourceState,
        FormatRetentionMode mode,
        string reviewedSelectionReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewedSelectionReference);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        string canonical = format.ToUpperInvariant();
        if (sourceState.Format != canonical) throw new ArgumentException("The retained source must match the instruction format.", nameof(sourceState));
        if ((mode == FormatRetentionMode.RetainInTarget) != (sourceState.RecordId == targetRecordId))
            throw new ArgumentException("The retention mode must match source ownership.", nameof(mode));
        Id = id;
        Format = canonical;
        TargetRecordId = targetRecordId;
        SourceState = sourceState;
        Mode = mode;
        ReviewedSelectionReference = reviewedSelectionReference;
        Preconditions = Array.AsReadOnly(RequiredPreconditions);
    }

    public string Id { get; }
    public string Format { get; }
    public CalibreBookId TargetRecordId { get; }
    public ExpectedFormatState SourceState { get; }
    public FormatRetentionMode Mode { get; }
    public string ReviewedSelectionReference { get; }
    public IReadOnlyList<string> Preconditions { get; }
}

public sealed record FormatRemovalInstruction
{
    public FormatRemovalInstruction(
        CalibreBookId recordId,
        string format,
        string relativePath,
        ExpectedFormatState expectedState,
        FormatRemovalReason reason,
        string retainedFormatInstructionId,
        string backupRequirementId,
        bool bytesIdenticalToRetainedSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ExpectedFormatState.ValidateRelativePath(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(retainedFormatInstructionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRequirementId);
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (expectedState.RecordId != recordId
            || !string.Equals(expectedState.Format, format, StringComparison.OrdinalIgnoreCase)
            || expectedState.RelativePath != relativePath.Replace('\\', '/'))
            throw new ArgumentException("A removal must reference its exact expected state.", nameof(expectedState));
        if (bytesIdenticalToRetainedSource != (reason == FormatRemovalReason.ByteIdenticalAlternative))
            throw new ArgumentException("Only exact-binary alternatives can be marked byte-identical.", nameof(bytesIdenticalToRetainedSource));
        RecordId = recordId;
        Format = format.ToUpperInvariant();
        RelativePath = relativePath.Replace('\\', '/');
        ExpectedState = expectedState;
        Reason = reason;
        RetainedFormatInstructionId = retainedFormatInstructionId;
        BackupRequirementId = backupRequirementId;
        BytesIdenticalToRetainedSource = bytesIdenticalToRetainedSource;
    }

    public CalibreBookId RecordId { get; }
    public string Format { get; }
    public string RelativePath { get; }
    public ExpectedFormatState ExpectedState { get; }
    public FormatRemovalReason Reason { get; }
    public string RetainedFormatInstructionId { get; }
    public string BackupRequirementId { get; }
    public bool BytesIdenticalToRetainedSource { get; }
}

public sealed record RecordRemovalInstruction
{
    private static readonly string[] RequiredPreconditions =
    [
        "The record must remain exactly in its expected state.",
        "The target record must survive.",
        "Every selected contribution must exist and be verified on the target before later removal.",
    ];

    public RecordRemovalInstruction(
        CalibreBookId recordId,
        IEnumerable<string> backupRequirementIds,
        IEnumerable<string> requiredRetainedFormatInstructionIds)
    {
        string[] backups = backupRequirementIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string[] retentions = requiredRetainedFormatInstructionIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (backups.Length == 0) throw new ArgumentException("A record removal requires backup coverage.", nameof(backupRequirementIds));
        RecordId = recordId;
        BackupRequirementIds = Array.AsReadOnly(backups);
        RequiredRetainedFormatInstructionIds = Array.AsReadOnly(retentions);
        Preconditions = Array.AsReadOnly(RequiredPreconditions);
    }

    public CalibreBookId RecordId { get; }
    public IReadOnlyList<string> BackupRequirementIds { get; }
    public IReadOnlyList<string> RequiredRetainedFormatInstructionIds { get; }
    public IReadOnlyList<string> Preconditions { get; }
}

public sealed record BackupRequirement
{
    public BackupRequirement(
        string id,
        BackupRequirementKind kind,
        CalibreBookId? recordId,
        string? format,
        bool required,
        string explanation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!required) throw new ArgumentException("Milestone 6 backup requirements must be mandatory.", nameof(required));
        Id = id;
        Kind = kind;
        RecordId = recordId;
        Format = string.IsNullOrWhiteSpace(format) ? null : format.ToUpperInvariant();
        Required = true;
        Explanation = explanation;
    }

    public string Id { get; }
    public BackupRequirementKind Kind { get; }
    public CalibreBookId? RecordId { get; }
    public string? Format { get; }
    public bool Required { get; }
    public string Explanation { get; }
}
