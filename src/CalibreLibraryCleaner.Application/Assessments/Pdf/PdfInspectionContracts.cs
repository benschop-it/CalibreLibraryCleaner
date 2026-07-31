using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Assessments.Pdf;

public sealed record PdfInspectionLimits(
    long MaximumFileBytes = 1024L * 1024 * 1024,
    long FileWarningBytes = 512L * 1024 * 1024,
    int MaximumHeaderBytes = 1024,
    int MaximumTailBytes = 1024 * 1024,
    int MaximumPages = 100_000,
    int MaximumSampledPages = 200,
    int MaximumObjects = 500_000,
    int ObjectWarningCount = 250_000,
    int MaximumStackDepth = 64,
    int StackDepthWarning = 48,
    long MaximumEncodedStreamBytes = 64L * 1024 * 1024,
    long EncodedStreamWarningBytes = 32L * 1024 * 1024,
    long MaximumDecodedStreamBytes = 64L * 1024 * 1024,
    long DecodedStreamWarningBytes = 32L * 1024 * 1024,
    long MaximumAggregateDecodedBytes = 512L * 1024 * 1024,
    long AggregateDecodedWarningBytes = 256L * 1024 * 1024,
    int MaximumMetadataFieldBytes = 64 * 1024,
    int MetadataFieldWarningBytes = 32 * 1024,
    int MaximumRetainedMetadataCharacters = 512,
    int MaximumXmpBytes = 4 * 1024 * 1024,
    int XmpWarningBytes = 1024 * 1024,
    int MaximumUsefulCharactersPerPage = 1_000_000,
    int UsefulCharactersPerPageWarning = 250_000,
    int MaximumUsefulCharactersPerSample = 10_000_000,
    int UsefulCharactersPerSampleWarning = 5_000_000,
    int MaximumFingerprintCharactersPerPage = 100_000,
    int MaximumEarlyIdentifierCharacters = 32_768,
    int MaximumImagesPerPage = 1_000,
    int ImagesPerPageWarning = 500,
    int MaximumImagesPerSample = 20_000,
    int ImagesPerSampleWarning = 10_000,
    int MaximumImageDimensionSamples = 100_000,
    int ImageDimensionSamplesWarning = 50_000,
    long MaximumDeclaredPixelsPerImage = 500_000_000,
    long DeclaredPixelsPerImageWarning = 100_000_000,
    int MaximumOperationsPerPage = 1_000_000,
    int OperationsPerPageWarning = 250_000,
    int MaximumOperationsPerSample = 5_000_000,
    int MaximumPageDimensionPoints = 72_000,
    int PageDimensionWarningPoints = 14_400,
    int MaximumOutlineNodes = 10_000,
    int OutlineNodeWarningCount = 5_000,
    int MaximumOutlineDepth = 64,
    int OutlineDepthWarning = 32,
    int MaximumRetainedExamples = 20,
    int MaximumRetainedIdentifiers = 20,
    int MaximumRetainedFindings = 128,
    int MaximumWorkerMessageBytes = 4 * 1024 * 1024,
    int WorkerMessageWarningBytes = 64 * 1024,
    int WallTimeSeconds = 60,
    int WallTimeWarningSeconds = 45,
    int CpuTimeSeconds = 45,
    int CpuTimeWarningSeconds = 30,
    long ManagedHeapBytes = 512L * 1024 * 1024,
    long ManagedHeapWarningBytes = 384L * 1024 * 1024,
    long WorkingSetBytes = 768L * 1024 * 1024,
    long WorkingSetWarningBytes = 512L * 1024 * 1024)
{
    public static PdfInspectionLimits V1 { get; } = new();

    public void Validate()
    {
        long[] longValues =
        [
            MaximumFileBytes, FileWarningBytes, MaximumEncodedStreamBytes, MaximumDecodedStreamBytes,
            EncodedStreamWarningBytes, DecodedStreamWarningBytes, MaximumAggregateDecodedBytes,
            AggregateDecodedWarningBytes, MaximumDeclaredPixelsPerImage, DeclaredPixelsPerImageWarning,
            ManagedHeapBytes, ManagedHeapWarningBytes, WorkingSetBytes, WorkingSetWarningBytes,
        ];
        int[] integerValues =
        [
            MaximumHeaderBytes, MaximumTailBytes, MaximumPages, MaximumSampledPages, MaximumObjects,
            MaximumStackDepth, StackDepthWarning, MaximumMetadataFieldBytes, MetadataFieldWarningBytes,
            MaximumRetainedMetadataCharacters, MaximumXmpBytes, XmpWarningBytes,
            MaximumUsefulCharactersPerPage, UsefulCharactersPerPageWarning, MaximumUsefulCharactersPerSample,
            UsefulCharactersPerSampleWarning, MaximumFingerprintCharactersPerPage,
            MaximumEarlyIdentifierCharacters, MaximumImagesPerPage, ImagesPerPageWarning, MaximumImagesPerSample,
            ImagesPerSampleWarning, MaximumImageDimensionSamples, ImageDimensionSamplesWarning,
            MaximumOperationsPerPage, OperationsPerPageWarning, MaximumOperationsPerSample,
            MaximumPageDimensionPoints, MaximumOutlineNodes, OutlineNodeWarningCount, MaximumOutlineDepth,
            OutlineDepthWarning,
            MaximumRetainedExamples, MaximumRetainedIdentifiers, MaximumRetainedFindings, MaximumWorkerMessageBytes,
            WorkerMessageWarningBytes, WallTimeSeconds, WallTimeWarningSeconds, CpuTimeSeconds, CpuTimeWarningSeconds,
        ];
        if (longValues.Any(value => value <= 0) || integerValues.Any(value => value <= 0)
            || ExceedsV1Ceilings()
            || MaximumSampledPages > 200 || MaximumRetainedExamples > 20 || MaximumRetainedIdentifiers > 20
            || MaximumRetainedFindings > 128 || FileWarningBytes > MaximumFileBytes
            || ObjectWarningCount <= 0 || ObjectWarningCount > MaximumObjects
            || StackDepthWarning > MaximumStackDepth
            || EncodedStreamWarningBytes > MaximumEncodedStreamBytes
            || DecodedStreamWarningBytes > MaximumDecodedStreamBytes
            || AggregateDecodedWarningBytes > MaximumAggregateDecodedBytes
            || MetadataFieldWarningBytes > MaximumMetadataFieldBytes
            || XmpWarningBytes > MaximumXmpBytes
            || UsefulCharactersPerPageWarning > MaximumUsefulCharactersPerPage
            || UsefulCharactersPerSampleWarning > MaximumUsefulCharactersPerSample
            || ImagesPerPageWarning > MaximumImagesPerPage
            || ImagesPerSampleWarning > MaximumImagesPerSample
            || ImageDimensionSamplesWarning > MaximumImageDimensionSamples
            || DeclaredPixelsPerImageWarning > MaximumDeclaredPixelsPerImage
            || OperationsPerPageWarning > MaximumOperationsPerPage
            || PageDimensionWarningPoints <= 0 || PageDimensionWarningPoints > MaximumPageDimensionPoints
            || OutlineNodeWarningCount > MaximumOutlineNodes
            || OutlineDepthWarning > MaximumOutlineDepth
            || WorkerMessageWarningBytes > MaximumWorkerMessageBytes
            || WallTimeWarningSeconds > WallTimeSeconds
            || CpuTimeWarningSeconds > CpuTimeSeconds
            || ManagedHeapWarningBytes > ManagedHeapBytes
            || WorkingSetWarningBytes > WorkingSetBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(PdfInspectionLimits), "PDF inspection limits are invalid or exceed V1 Domain bounds.");
        }
    }

    private bool ExceedsV1Ceilings()
    {
        PdfInspectionLimits ceiling = V1;
        return MaximumFileBytes > ceiling.MaximumFileBytes
            || FileWarningBytes > ceiling.FileWarningBytes
            || MaximumHeaderBytes > ceiling.MaximumHeaderBytes
            || MaximumTailBytes > ceiling.MaximumTailBytes
            || MaximumPages > ceiling.MaximumPages
            || MaximumSampledPages > ceiling.MaximumSampledPages
            || MaximumObjects > ceiling.MaximumObjects
            || ObjectWarningCount > ceiling.ObjectWarningCount
            || MaximumStackDepth > ceiling.MaximumStackDepth
            || StackDepthWarning > ceiling.StackDepthWarning
            || MaximumEncodedStreamBytes > ceiling.MaximumEncodedStreamBytes
            || EncodedStreamWarningBytes > ceiling.EncodedStreamWarningBytes
            || MaximumDecodedStreamBytes > ceiling.MaximumDecodedStreamBytes
            || DecodedStreamWarningBytes > ceiling.DecodedStreamWarningBytes
            || MaximumAggregateDecodedBytes > ceiling.MaximumAggregateDecodedBytes
            || AggregateDecodedWarningBytes > ceiling.AggregateDecodedWarningBytes
            || MaximumMetadataFieldBytes > ceiling.MaximumMetadataFieldBytes
            || MetadataFieldWarningBytes > ceiling.MetadataFieldWarningBytes
            || MaximumRetainedMetadataCharacters > ceiling.MaximumRetainedMetadataCharacters
            || MaximumXmpBytes > ceiling.MaximumXmpBytes
            || XmpWarningBytes > ceiling.XmpWarningBytes
            || MaximumUsefulCharactersPerPage > ceiling.MaximumUsefulCharactersPerPage
            || UsefulCharactersPerPageWarning > ceiling.UsefulCharactersPerPageWarning
            || MaximumUsefulCharactersPerSample > ceiling.MaximumUsefulCharactersPerSample
            || UsefulCharactersPerSampleWarning > ceiling.UsefulCharactersPerSampleWarning
            || MaximumFingerprintCharactersPerPage > ceiling.MaximumFingerprintCharactersPerPage
            || MaximumEarlyIdentifierCharacters > ceiling.MaximumEarlyIdentifierCharacters
            || MaximumImagesPerPage > ceiling.MaximumImagesPerPage
            || ImagesPerPageWarning > ceiling.ImagesPerPageWarning
            || MaximumImagesPerSample > ceiling.MaximumImagesPerSample
            || ImagesPerSampleWarning > ceiling.ImagesPerSampleWarning
            || MaximumImageDimensionSamples > ceiling.MaximumImageDimensionSamples
            || ImageDimensionSamplesWarning > ceiling.ImageDimensionSamplesWarning
            || MaximumDeclaredPixelsPerImage > ceiling.MaximumDeclaredPixelsPerImage
            || DeclaredPixelsPerImageWarning > ceiling.DeclaredPixelsPerImageWarning
            || MaximumOperationsPerPage > ceiling.MaximumOperationsPerPage
            || OperationsPerPageWarning > ceiling.OperationsPerPageWarning
            || MaximumOperationsPerSample > ceiling.MaximumOperationsPerSample
            || MaximumPageDimensionPoints > ceiling.MaximumPageDimensionPoints
            || PageDimensionWarningPoints > ceiling.PageDimensionWarningPoints
            || MaximumOutlineNodes > ceiling.MaximumOutlineNodes
            || OutlineNodeWarningCount > ceiling.OutlineNodeWarningCount
            || MaximumOutlineDepth > ceiling.MaximumOutlineDepth
            || OutlineDepthWarning > ceiling.OutlineDepthWarning
            || MaximumRetainedExamples > ceiling.MaximumRetainedExamples
            || MaximumRetainedIdentifiers > ceiling.MaximumRetainedIdentifiers
            || MaximumRetainedFindings > ceiling.MaximumRetainedFindings
            || MaximumWorkerMessageBytes > ceiling.MaximumWorkerMessageBytes
            || WorkerMessageWarningBytes > ceiling.WorkerMessageWarningBytes
            || WallTimeSeconds > ceiling.WallTimeSeconds
            || WallTimeWarningSeconds > ceiling.WallTimeWarningSeconds
            || CpuTimeSeconds > ceiling.CpuTimeSeconds
            || CpuTimeWarningSeconds > ceiling.CpuTimeWarningSeconds
            || ManagedHeapBytes > ceiling.ManagedHeapBytes
            || ManagedHeapWarningBytes > ceiling.ManagedHeapWarningBytes
            || WorkingSetBytes > ceiling.WorkingSetBytes
            || WorkingSetWarningBytes > ceiling.WorkingSetWarningBytes;
    }
}

public sealed record PdfInspectionRequest(
    CalibreBookId BookId,
    string LibraryRoot,
    string FullPath,
    string ExpectedRelativePath,
    FormatFileFingerprint Fingerprint,
    FormatFileObservation Observation,
    PdfInspectionLimits Limits);

public sealed record PdfDocumentHeaderFacts(
    CalibreBookId BookId,
    string ExpectedRelativePath,
    int PageCount,
    IReadOnlyList<int> OutlineTargetPages);

public sealed record PdfInspectionProgress(string Stage, int CompletedPages, int TotalPages, bool ResourceWarning);

public enum PdfInspectionProblemCode
{
    MissingFile,
    InaccessibleFile,
    UnsafePath,
    ChangedFile,
    ZeroLength,
    InvalidSignature,
    TruncatedFile,
    MalformedStructure,
    PasswordRequired,
    UnsupportedEncryption,
    PageTreeUnavailable,
    ZeroPages,
    PageUnreadable,
    MalformedFont,
    MalformedMetadata,
    MalformedOutline,
    UnsupportedFilter,
    UnsupportedStructure,
    StackDepthExceeded,
    ObjectLimitExceeded,
    StreamLimitExceeded,
    PageLimitExceeded,
    DimensionLimitExceeded,
    ImageLimitExceeded,
    OperationLimitExceeded,
    MetadataLimitExceeded,
    ParserTimeout,
    CpuLimitExceeded,
    MemoryLimitExceeded,
    WorkerCrashed,
    WorkerProtocolInvalid,
    IncompleteInspection,
}

public sealed record PdfInspectionProblem(
    PdfInspectionProblemCode Code,
    int? PageNumber = null,
    string? EvidenceKey = null);

public sealed record PdfPageFacts(
    int PageNumber,
    bool Parsed,
    int UsefulCharacterCount,
    int GlyphAreaCoverageBasisPoints,
    int ImageCount,
    int MaximumImageCoverageBasisPoints,
    int AggregateImageCoverageBasisPoints,
    int NontrivialOperationCount,
    int WidthMilliPoints,
    int HeightMilliPoints,
    string? SharedContentIdentity,
    string? NormalizedTextFingerprint,
    bool TextFingerprintComplete,
    string? SharedImageIdentity,
    string? DominantImageGeometryKey,
    bool CountersCapped,
    bool ResourceHeavy,
    bool FontOrFilterUnsupported = false,
    bool FontEvidenceReliable = true,
    bool HiddenTextPresent = false)
{
    public bool HasUsefulText => UsefulCharacterCount >= 200
        || UsefulCharacterCount >= 50 && GlyphAreaCoverageBasisPoints >= 50;
    public bool IsImageDominant => MaximumImageCoverageBasisPoints >= 7_000 || AggregateImageCoverageBasisPoints >= 8_000;
    public bool IsTextDominant => HasUsefulText && AggregateImageCoverageBasisPoints < 5_000;
    public bool IsContentful => UsefulCharacterCount >= 10 || AggregateImageCoverageBasisPoints >= 500 || NontrivialOperationCount >= 10;
    public bool IsSuspiciousBlank => Parsed && !CountersCapped && UsefulCharacterCount < 10
        && AggregateImageCoverageBasisPoints < 100 && NontrivialOperationCount < 5
        && WidthMilliPoints > 0 && HeightMilliPoints > 0;
}

public sealed record PdfInspectionResult(
    CalibreBookId BookId,
    string ExpectedRelativePath,
    PdfOpenStatus OpenStatus,
    PdfEncryptionStatus EncryptionStatus,
    int? PageCount,
    string? PdfVersion,
    PdfDocumentMetadataSummary Metadata,
    bool OutlinePresent,
    int OutlineEntryCount,
    PdfActiveContentSummary ActiveContent,
    IReadOnlyList<int> SelectedPages,
    IReadOnlyList<PdfPageFacts> PageFacts,
    IReadOnlyList<PdfIdentifierEvidence> Identifiers,
    int InvalidIdentifierCandidateCount,
    int ObjectCount,
    long AggregateDecodedBytes,
    bool ResourceCountsWithinSoftLimits,
    bool FactsIncomplete,
    IReadOnlyList<PdfInspectionProblem> Problems)
{
    public static PdfInspectionResult Failed(
        CalibreBookId bookId,
        string expectedRelativePath,
        PdfInspectionProblemCode problem,
        PdfOpenStatus openStatus = PdfOpenStatus.Unreadable,
        PdfEncryptionStatus encryptionStatus = PdfEncryptionStatus.Unknown) => new(
        bookId,
        expectedRelativePath,
        openStatus,
        encryptionStatus,
        null,
        null,
        PdfDocumentMetadataSummary.Empty,
        false,
        0,
        new(0, 0, 0),
        [],
        [],
        [],
        0,
        0,
        0,
        false,
        true,
        [new(problem)]);
}

public sealed record PdfAssessmentTarget(
    CalibreBookId BookId,
    string Format,
    string ExpectedRelativePath,
    string? LibraryRoot,
    string? FullPath,
    FormatFileStatus FileStatus,
    FormatFileFingerprint? Fingerprint,
    FormatFileObservation? Observation);

public sealed record PdfAssessmentProgress(
    int CompletedFiles,
    int TotalFiles,
    string CurrentRelativePath,
    string Stage,
    int CompletedPages = 0,
    int TotalPages = 0);
