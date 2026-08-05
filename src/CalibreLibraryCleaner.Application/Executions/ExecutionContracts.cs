using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Executions;

public sealed record CalibreToolDescriptor
{
    public CalibreToolDescriptor(
        string canonicalExecutablePath,
        ExecutionToolIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalExecutablePath);
        CanonicalExecutablePath = canonicalExecutablePath;
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public string CanonicalExecutablePath { get; }
    public ExecutionToolIdentity Identity { get; }
}

public sealed record CalibreToolDiscoveryResult(
    CalibreToolDescriptor? Tool,
    IReadOnlyList<ExecutionIssue> Issues)
{
    public bool IsSuccess => Tool is not null
        && Issues.All(value => value.Severity != ExecutionIssueSeverity.BlockingError);
}

public sealed record CalibreCommandResult(
    string CommandKind,
    bool Started,
    int? ExitCode,
    IReadOnlyList<string> SanitizedArguments,
    string SanitizedStandardOutput,
    string SanitizedStandardError,
    TimeSpan Duration,
    string? FailureCode = null)
{
    public bool IsSuccess => Started && ExitCode == 0 && FailureCode is null;
}

public sealed record OpenCalibreMutationWorkerRequest(
    CalibreToolDescriptor Tool,
    string LibraryRoot,
    string ExpectedLibraryUuid);

public sealed record CalibreMutationWorkerOpenResult(
    ICalibreMutationWorkerSession? Session,
    string? FailureCode)
{
    public bool IsSuccess => Session is not null && FailureCode is null;
}

public enum CalibreMutationOperationKind
{
    TransferFormat,
    RemoveFormat,
    RemoveRecord,
}

public sealed record CalibreMutationOperation
{
    private CalibreMutationOperation(
        string operationId,
        CalibreMutationOperationKind kind,
        CalibreBookId recordId,
        string? canonicalFormat,
        CalibreBookId? targetRecordId,
        FormatFileFingerprint? expectedFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (operationId.Length > 160) throw new ArgumentOutOfRangeException(nameof(operationId));
        if (recordId.Value <= 0) throw new ArgumentOutOfRangeException(nameof(recordId));
        if (kind != CalibreMutationOperationKind.RemoveRecord)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(canonicalFormat);
            if (canonicalFormat.Length > 16 || !canonicalFormat.All(char.IsAsciiLetterOrDigit))
                throw new ArgumentException("The canonical format is invalid.", nameof(canonicalFormat));
        }
        if (kind == CalibreMutationOperationKind.TransferFormat
            && (targetRecordId is null || targetRecordId.Value.Value <= 0
                || targetRecordId == recordId || expectedFingerprint is null))
            throw new ArgumentException("The transfer operation is incomplete.", nameof(targetRecordId));

        OperationId = operationId;
        Kind = kind;
        RecordId = recordId;
        CanonicalFormat = canonicalFormat?.ToUpperInvariant();
        TargetRecordId = targetRecordId;
        ExpectedFingerprint = expectedFingerprint;
    }

    public string OperationId { get; }
    public CalibreMutationOperationKind Kind { get; }
    public CalibreBookId RecordId { get; }
    public string? CanonicalFormat { get; }
    public CalibreBookId? TargetRecordId { get; }
    public FormatFileFingerprint? ExpectedFingerprint { get; }

    public static CalibreMutationOperation TransferFormat(
        string operationId,
        CalibreBookId sourceRecordId,
        CalibreBookId targetRecordId,
        string canonicalFormat,
        FormatFileFingerprint expectedFingerprint) => new(operationId,
        CalibreMutationOperationKind.TransferFormat, sourceRecordId, canonicalFormat,
        targetRecordId, expectedFingerprint ?? throw new ArgumentNullException(nameof(expectedFingerprint)));

    public static CalibreMutationOperation RemoveFormat(
        string operationId,
        CalibreBookId recordId,
        string canonicalFormat) => new(operationId, CalibreMutationOperationKind.RemoveFormat,
        recordId, canonicalFormat, null, null);

    public static CalibreMutationOperation RemoveRecord(
        string operationId,
        CalibreBookId recordId) => new(operationId, CalibreMutationOperationKind.RemoveRecord,
        recordId, null, null, null);
}

public sealed record CalibreMutationChunkRequest
{
    public const int MaximumOperationCount = 100;

    public CalibreMutationChunkRequest(string chunkId, IReadOnlyList<CalibreMutationOperation> operations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chunkId);
        ArgumentNullException.ThrowIfNull(operations);
        if (chunkId.Length > 100) throw new ArgumentOutOfRangeException(nameof(chunkId));
        if (operations.Count is < 1 or > MaximumOperationCount)
            throw new ArgumentOutOfRangeException(nameof(operations));
        if (operations.Select(value => value.OperationId).Distinct(StringComparer.Ordinal).Count() != operations.Count)
            throw new ArgumentException("Mutation operation IDs must be unique within a chunk.", nameof(operations));

        ChunkId = chunkId;
        Operations = Array.AsReadOnly(operations.ToArray());
    }

    public string ChunkId { get; }
    public IReadOnlyList<CalibreMutationOperation> Operations { get; }
}

public sealed record CalibreMutationOperationResult(
    string OperationId,
    CalibreMutationOperationKind Kind,
    bool IsSuccess,
    string? FailureCode = null);

public sealed record CalibreMutationChunkResult(
    string ChunkId,
    bool MutationStarted,
    IReadOnlyList<CalibreMutationOperationResult> OperationResults,
    string? FailureCode = null)
{
    public bool IsSuccess => FailureCode is null
        && OperationResults.Count > 0
        && OperationResults.All(value => value.IsSuccess && value.FailureCode is null);
}
