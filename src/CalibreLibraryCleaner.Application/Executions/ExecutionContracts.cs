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
    string ExpectedLibraryUuid,
    string? MetadataCoverStagingRoot = null,
    string? QuarantineDestinationRoot = null,
    string? ExpectedQuarantineLibraryUuid = null);

public sealed record CalibreMutationWorkerOpenResult(
    ICalibreMutationWorkerSession? Session,
    string? FailureCode,
    string? QuarantineLibraryUuid = null,
    bool? QuarantineLibraryExisted = null)
{
    public bool IsSuccess => Session is not null && FailureCode is null;
}

public enum CalibreMutationOperationKind
{
    SetMetadata,
    RefreshManagedName,
    TransferFormat,
    RemoveFormat,
    RemoveRecord,
    CopyRecordToQuarantine,
    RemoveRecordWithContent,
}

public sealed record CalibreQuarantineFormatExpectation
{
    public CalibreQuarantineFormatExpectation(
        string format,
        string expectedRelativePath,
        FormatFileFingerprint fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRelativePath);
        if (format.Length > 16 || !format.All(char.IsAsciiLetterOrDigit)
            || expectedRelativePath.Length > 1_024
            || expectedRelativePath.Any(char.IsControl))
            throw new ArgumentException("The quarantine format expectation is invalid.");
        Format = format.ToUpperInvariant();
        ExpectedRelativePath = expectedRelativePath;
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
    }

    public string Format { get; }
    public string ExpectedRelativePath { get; }
    public FormatFileFingerprint Fingerprint { get; }
}

public sealed record CalibreQuarantineRecordExpectation
{
    public CalibreQuarantineRecordExpectation(
        string title,
        IEnumerable<string> authors,
        IEnumerable<string> authorSorts,
        IEnumerable<CalibreQuarantineFormatExpectation> formats)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(authors);
        ArgumentNullException.ThrowIfNull(authorSorts);
        ArgumentNullException.ThrowIfNull(formats);
        string[] authorValues = authors.ToArray();
        string[] sortValues = authorSorts.ToArray();
        CalibreQuarantineFormatExpectation[] formatValues = formats
            .OrderBy(value => value.Format, StringComparer.Ordinal)
            .ToArray();
        if (title.Length > 2_048 || title.Any(char.IsControl)
            || authorValues.Length is < 1 or > 32
            || sortValues.Length != authorValues.Length
            || authorValues.Concat(sortValues).Any(value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            || formatValues.Length > 32
            || formatValues.Select(value => value.Format).Distinct(StringComparer.Ordinal).Count()
                != formatValues.Length)
            throw new ArgumentException("The quarantine record expectation is invalid.");
        Title = title;
        Authors = Array.AsReadOnly(authorValues);
        AuthorSorts = Array.AsReadOnly(sortValues);
        Formats = Array.AsReadOnly(formatValues);
    }

    public string Title { get; }
    public IReadOnlyList<string> Authors { get; }
    public IReadOnlyList<string> AuthorSorts { get; }
    public IReadOnlyList<CalibreQuarantineFormatExpectation> Formats { get; }
}

public sealed record CalibreMetadataSourceIdentity
{
    public CalibreMetadataSourceIdentity(
        string providerId,
        string providerVersion,
        string editionId,
        string proposalPolicyVersion)
    {
        ProviderId = Bound(providerId, 64, nameof(providerId));
        ProviderVersion = Bound(providerVersion, 128, nameof(providerVersion));
        EditionId = Bound(editionId, 160, nameof(editionId));
        ProposalPolicyVersion = Bound(proposalPolicyVersion, 128, nameof(proposalPolicyVersion));
    }

    public string ProviderId { get; }
    public string ProviderVersion { get; }
    public string EditionId { get; }
    public string ProposalPolicyVersion { get; }

    private static string Bound(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        return normalized.Length <= maximumLength && normalized.All(character => !char.IsControl(character))
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed record CalibreMetadataMutation
{
    public CalibreMetadataMutation(
        LibraryMetadataField field,
        IEnumerable<string> values,
        CalibreMetadataSourceIdentity source,
        string? stagedCoverFileName = null,
        FormatFileFingerprint? stagedCoverFingerprint = null,
        IEnumerable<string>? authorSortValues = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(field)) throw new ArgumentOutOfRangeException(nameof(field));
        string[] boundedValues = values.Select(value => value ?? throw new ArgumentException(
                "Metadata values cannot contain null.", nameof(values)))
            .ToArray();
        if (boundedValues.Length > 32 || boundedValues.Any(value => value.Length > 2_048)
            || boundedValues.Sum(value => value.Length) > 16_384)
            throw new ArgumentException("Metadata values exceed their bounds.", nameof(values));
        bool cover = field == LibraryMetadataField.Cover;
        if (cover != (stagedCoverFingerprint is not null)
            || cover != !string.IsNullOrWhiteSpace(stagedCoverFileName)
            || cover && boundedValues.Length != 0
            || !cover && boundedValues.Length == 0)
            throw new ArgumentException("The metadata cover payload is invalid.", nameof(stagedCoverFileName));
        if (stagedCoverFileName is not null
            && (stagedCoverFileName.Length > 128
                || stagedCoverFileName != Path.GetFileName(stagedCoverFileName)))
            throw new ArgumentException("The staged cover filename is invalid.", nameof(stagedCoverFileName));
        string[]? boundedAuthorSorts = authorSortValues?.Select(value => value
            ?? throw new ArgumentException("Author sorts cannot contain null.", nameof(authorSortValues))).ToArray();
        if (boundedAuthorSorts is not null
                && field != LibraryMetadataField.Authors
            || boundedAuthorSorts is not null
                && (boundedAuthorSorts.Length != boundedValues.Length
                    || boundedAuthorSorts.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512)))
            throw new ArgumentException("Metadata author sorts are invalid.", nameof(authorSortValues));
        Field = field;
        Values = Array.AsReadOnly(boundedValues);
        Source = source;
        StagedCoverFileName = stagedCoverFileName;
        StagedCoverFingerprint = stagedCoverFingerprint;
        AuthorSortValues = boundedAuthorSorts is null ? null : Array.AsReadOnly(boundedAuthorSorts);
    }

    public LibraryMetadataField Field { get; }
    public IReadOnlyList<string> Values { get; }
    public CalibreMetadataSourceIdentity Source { get; }
    public string? StagedCoverFileName { get; }
    public FormatFileFingerprint? StagedCoverFingerprint { get; }
    public IReadOnlyList<string>? AuthorSortValues { get; }
}

public sealed record CalibreMutationOperation
{
    private CalibreMutationOperation(
        string operationId,
        CalibreMutationOperationKind kind,
        CalibreBookId recordId,
        string? canonicalFormat,
        CalibreBookId? targetRecordId,
        FormatFileFingerprint? expectedFingerprint,
        CalibreMetadataMutation? metadata,
        CalibreQuarantineRecordExpectation? quarantineExpectation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        if (operationId.Length > 160) throw new ArgumentOutOfRangeException(nameof(operationId));
        if (recordId.Value <= 0) throw new ArgumentOutOfRangeException(nameof(recordId));
        if (kind is CalibreMutationOperationKind.TransferFormat or CalibreMutationOperationKind.RemoveFormat)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(canonicalFormat);
            if (canonicalFormat.Length > 16 || !canonicalFormat.All(char.IsAsciiLetterOrDigit))
                throw new ArgumentException("The canonical format is invalid.", nameof(canonicalFormat));
        }
        if (kind == CalibreMutationOperationKind.TransferFormat
            && (targetRecordId is null || targetRecordId.Value.Value <= 0
                || targetRecordId == recordId || expectedFingerprint is null))
            throw new ArgumentException("The transfer operation is incomplete.", nameof(targetRecordId));
        if ((kind == CalibreMutationOperationKind.SetMetadata) != (metadata is not null)
            || kind == CalibreMutationOperationKind.SetMetadata
                && (canonicalFormat is not null || targetRecordId is not null || expectedFingerprint is not null))
            throw new ArgumentException("The metadata operation is invalid.", nameof(metadata));
        if (kind == CalibreMutationOperationKind.RefreshManagedName
            && (canonicalFormat is not null || targetRecordId is not null
                || expectedFingerprint is not null || metadata is not null))
            throw new ArgumentException("The managed-name refresh operation is invalid.", nameof(kind));
        bool quarantine = kind is CalibreMutationOperationKind.CopyRecordToQuarantine
            or CalibreMutationOperationKind.RemoveRecordWithContent;
        if (quarantine != (quarantineExpectation is not null)
            || quarantine && (canonicalFormat is not null || targetRecordId is not null
                || expectedFingerprint is not null || metadata is not null))
            throw new ArgumentException("The quarantine operation is invalid.", nameof(kind));

        OperationId = operationId;
        Kind = kind;
        RecordId = recordId;
        CanonicalFormat = canonicalFormat?.ToUpperInvariant();
        TargetRecordId = targetRecordId;
        ExpectedFingerprint = expectedFingerprint;
        Metadata = metadata;
        QuarantineExpectation = quarantineExpectation;
    }

    public string OperationId { get; }
    public CalibreMutationOperationKind Kind { get; }
    public CalibreBookId RecordId { get; }
    public string? CanonicalFormat { get; }
    public CalibreBookId? TargetRecordId { get; }
    public FormatFileFingerprint? ExpectedFingerprint { get; }
    public CalibreMetadataMutation? Metadata { get; }
    public CalibreQuarantineRecordExpectation? QuarantineExpectation { get; }

    public static CalibreMutationOperation SetMetadata(
        string operationId,
        CalibreBookId recordId,
        CalibreMetadataMutation metadata) => new(operationId,
        CalibreMutationOperationKind.SetMetadata, recordId, null, null, null,
        metadata ?? throw new ArgumentNullException(nameof(metadata)), null);

    public static CalibreMutationOperation TransferFormat(
        string operationId,
        CalibreBookId sourceRecordId,
        CalibreBookId targetRecordId,
        string canonicalFormat,
        FormatFileFingerprint expectedFingerprint) => new(operationId,
        CalibreMutationOperationKind.TransferFormat, sourceRecordId, canonicalFormat,
        targetRecordId, expectedFingerprint ?? throw new ArgumentNullException(nameof(expectedFingerprint)), null, null);

    public static CalibreMutationOperation RefreshManagedName(
        string operationId,
        CalibreBookId recordId) => new(
        operationId, CalibreMutationOperationKind.RefreshManagedName, recordId,
        null, null, null, null, null);

    public static CalibreMutationOperation RemoveFormat(
        string operationId,
        CalibreBookId recordId,
        string canonicalFormat) => new(operationId, CalibreMutationOperationKind.RemoveFormat,
        recordId, canonicalFormat, null, null, null, null);

    public static CalibreMutationOperation RemoveRecord(
        string operationId,
        CalibreBookId recordId) => new(operationId, CalibreMutationOperationKind.RemoveRecord,
        recordId, null, null, null, null, null);

    public static CalibreMutationOperation CopyRecordToQuarantine(
        string operationId,
        CalibreBookId recordId,
        CalibreQuarantineRecordExpectation expectation) => new(
        operationId,
        CalibreMutationOperationKind.CopyRecordToQuarantine,
        recordId,
        null,
        null,
        null,
        null,
        expectation ?? throw new ArgumentNullException(nameof(expectation)));

    public static CalibreMutationOperation RemoveRecordWithContent(
        string operationId,
        CalibreBookId recordId,
        CalibreQuarantineRecordExpectation expectation) => new(
        operationId,
        CalibreMutationOperationKind.RemoveRecordWithContent,
        recordId,
        null,
        null,
        null,
        null,
        expectation ?? throw new ArgumentNullException(nameof(expectation)));
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
    string? FailureCode = null,
    IReadOnlyList<string>? VerifiedMetadataValues = null,
    string? VerifiedManagedPath = null,
    string? VerifiedAuthorSort = null,
    IReadOnlyList<string>? VerifiedAuthorSortValues = null,
    IReadOnlyList<ManagedFormatReadBack>? VerifiedFormats = null,
    bool IsSkipped = false,
    string? SkipCode = null,
    CalibreBookId? QuarantineRecordId = null);

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
