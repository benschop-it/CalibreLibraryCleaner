using System.Collections.ObjectModel;

namespace CalibreLibraryCleaner.Domain.Assessments;

public sealed record PdfSamplingSummary
{
    public PdfSamplingSummary(
        PdfSamplingMode mode,
        int totalPageCount,
        IEnumerable<int> selectedPages,
        int successfullyAnalyzedPages,
        PdfPolicyVersion policyVersion)
    {
        ArgumentNullException.ThrowIfNull(selectedPages);
        ArgumentNullException.ThrowIfNull(policyVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalPageCount);
        if (totalPageCount > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(totalPageCount), "PDF page count exceeds the V1 semantic bound.");
        }
        int[] pages = selectedPages.Distinct().Order().ToArray();
        if (pages.Length == 0 || pages.Length > 200 || pages.Any(page => page <= 0 || page > totalPageCount))
        {
            throw new ArgumentException("The PDF sample must contain 1 through 200 valid unique pages.", nameof(selectedPages));
        }

        if (successfullyAnalyzedPages < 0 || successfullyAnalyzedPages > pages.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(successfullyAnalyzedPages));
        }

        if ((mode == PdfSamplingMode.AllPages) != (pages.Length == totalPageCount))
        {
            throw new ArgumentException("All-pages sampling must disclose every page exactly.", nameof(mode));
        }

        Mode = mode;
        TotalPageCount = totalPageCount;
        SelectedPages = new ReadOnlyCollection<int>(pages);
        SuccessfullyAnalyzedPages = successfullyAnalyzedPages;
        PolicyVersion = policyVersion;
    }

    public PdfSamplingMode Mode { get; }
    public int TotalPageCount { get; }
    public IReadOnlyList<int> SelectedPages { get; }
    public int SuccessfullyAnalyzedPages { get; }
    public PdfPolicyVersion PolicyVersion { get; }
    public bool InspectedAllPages => Mode == PdfSamplingMode.AllPages;
    public int RequestedPageCount => SelectedPages.Count;
}

public sealed record PdfTextSummary(
    PdfTextExtractionStatus ExtractionStatus,
    int AnalyzablePageCount,
    int TextBearingPageCount,
    int UsefulTextPageCount,
    int UsefulCharacterCount,
    int UsefulTextPercentageBasisPoints,
    PdfTextDensityBand DensityBand,
    bool CountCapped)
{
    public PdfTextSummary Validate()
    {
        ValidateCount(AnalyzablePageCount);
        ValidateCount(TextBearingPageCount);
        ValidateCount(UsefulTextPageCount);
        ValidateCount(UsefulCharacterCount);
        if (AnalyzablePageCount > 200 || UsefulCharacterCount > 10_000_000
            || TextBearingPageCount > AnalyzablePageCount || UsefulTextPageCount > AnalyzablePageCount
            || UsefulTextPercentageBasisPoints is < 0 or > 10_000)
        {
            throw new ArgumentException("PDF text summary counts or percentages are inconsistent.");
        }

        return this;
    }

    private static void ValidateCount(int value) => ArgumentOutOfRangeException.ThrowIfNegative(value);
}

public sealed record PdfImageSummary(
    int AnalyzablePageCount,
    int ImageBearingPageCount,
    int ImageDominantPageCount,
    int ImageCount,
    int ImageDominantPercentageBasisPoints,
    PdfContentBalance ContentBalance,
    bool CountCapped)
{
    public PdfImageSummary Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(AnalyzablePageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(ImageBearingPageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(ImageDominantPageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(ImageCount);
        if (AnalyzablePageCount > 200 || ImageCount > 20_000
            || ImageBearingPageCount > AnalyzablePageCount || ImageDominantPageCount > AnalyzablePageCount
            || ImageDominantPercentageBasisPoints is < 0 or > 10_000)
        {
            throw new ArgumentException("PDF image summary counts or percentages are inconsistent.");
        }

        return this;
    }
}

public sealed record PdfPageAnalysisSummary
{
    public PdfPageAnalysisSummary(
        int requestedPages,
        int analyzedPages,
        int failedPages,
        int suspiciousBlankPages,
        int unusualDimensionPages,
        int resourceHeavyPages,
        IEnumerable<int>? failedPageExamples = null,
        IEnumerable<int>? blankPageExamples = null,
        IEnumerable<int>? unusualDimensionExamples = null,
        IEnumerable<int>? resourceHeavyExamples = null)
    {
        int[] counts = [requestedPages, analyzedPages, failedPages, suspiciousBlankPages, unusualDimensionPages, resourceHeavyPages];
        if (counts.Any(value => value < 0) || requestedPages > 200 || analyzedPages + failedPages != requestedPages)
        {
            throw new ArgumentException("PDF page-analysis counts are inconsistent.");
        }

        RequestedPages = requestedPages;
        AnalyzedPages = analyzedPages;
        FailedPages = failedPages;
        SuspiciousBlankPages = suspiciousBlankPages;
        UnusualDimensionPages = unusualDimensionPages;
        ResourceHeavyPages = resourceHeavyPages;
        FailedPageExamples = CopyExamples(failedPageExamples);
        BlankPageExamples = CopyExamples(blankPageExamples);
        UnusualDimensionExamples = CopyExamples(unusualDimensionExamples);
        ResourceHeavyExamples = CopyExamples(resourceHeavyExamples);
    }

    public int RequestedPages { get; }
    public int AnalyzedPages { get; }
    public int FailedPages { get; }
    public int SuspiciousBlankPages { get; }
    public int UnusualDimensionPages { get; }
    public int ResourceHeavyPages { get; }
    public IReadOnlyList<int> FailedPageExamples { get; }
    public IReadOnlyList<int> BlankPageExamples { get; }
    public IReadOnlyList<int> UnusualDimensionExamples { get; }
    public IReadOnlyList<int> ResourceHeavyExamples { get; }

    private static ReadOnlyCollection<int> CopyExamples(IEnumerable<int>? values)
    {
        int[] result = (values ?? []).Distinct().Order().Take(20).ToArray();
        if (result.Any(value => value <= 0))
        {
            throw new ArgumentException("PDF page examples must be positive.", nameof(values));
        }

        return new ReadOnlyCollection<int>(result);
    }
}

public sealed record PdfActiveContentSummary(
    int JavaScriptOrActionMarkers,
    int ExternalReferenceMarkers,
    int EmbeddedFileMarkers)
{
    public PdfActiveContentSummary Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(JavaScriptOrActionMarkers);
        ArgumentOutOfRangeException.ThrowIfNegative(ExternalReferenceMarkers);
        ArgumentOutOfRangeException.ThrowIfNegative(EmbeddedFileMarkers);
        if (JavaScriptOrActionMarkers > 10_000 || ExternalReferenceMarkers > 10_000 || EmbeddedFileMarkers > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(JavaScriptOrActionMarkers), "PDF active-content counters exceed the retained bound.");
        }

        return this;
    }
}

public sealed record PdfFeatureSummary
{
    public PdfFeatureSummary(
        PdfOpenStatus openStatus,
        PdfEncryptionStatus encryptionStatus,
        int? pageCount,
        string? pdfVersion,
        PdfDocumentMetadataSummary metadata,
        PdfTextSummary text,
        PdfImageSummary images,
        bool outlinePresent,
        int outlineEntryCount,
        PdfActiveContentSummary activeContent,
        PdfPageAnalysisSummary pageAnalysis,
        IEnumerable<PdfRepeatedPageCluster>? repeatedPages,
        IEnumerable<PdfIdentifierEvidence>? identifiers,
        PdfDocumentClassification classification,
        PdfClassificationConfidence classificationConfidence,
        PdfSamplingSummary? sampling,
        PdfPolicyVersion classificationPolicyVersion,
        PdfPolicyVersion resourceProfileVersion,
        bool factsIncomplete = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(outlineEntryCount);
        if (pageCount > 100_000 || outlineEntryCount > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount), "PDF feature counts exceed V1 semantic bounds.");
        }
        if (pageCount <= 0)
        {
            pageCount = null;
        }

        string? boundedVersion = string.IsNullOrWhiteSpace(pdfVersion) ? null : pdfVersion.Trim();
        if (boundedVersion is { Length: > 32 })
        {
            throw new ArgumentException("PDF version text is bounded.", nameof(pdfVersion));
        }

        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(activeContent);
        ArgumentNullException.ThrowIfNull(pageAnalysis);
        ArgumentNullException.ThrowIfNull(classificationPolicyVersion);
        ArgumentNullException.ThrowIfNull(resourceProfileVersion);
        text.Validate();
        images.Validate();
        activeContent.Validate();
        PdfRepeatedPageCluster[] repeats = (repeatedPages ?? [])
            .OrderBy(value => value.Evidence)
            .ThenBy(value => value.PageNumbers[0])
            .Take(20)
            .ToArray();
        PdfIdentifierEvidence[] identifierValues = (identifiers ?? [])
            .GroupBy(value => value.NormalizedIsbn, StringComparer.Ordinal)
            .Select(group => group.OrderBy(value => value.Source).ThenBy(value => value.PageNumber).First())
            .OrderBy(value => value.NormalizedIsbn, StringComparer.Ordinal)
            .ThenBy(value => value.Source)
            .ThenBy(value => value.PageNumber)
            .Take(20)
            .ToArray();
        if (sampling is not null && pageCount != sampling.TotalPageCount)
        {
            throw new ArgumentException("PDF page count and sampling total must agree.", nameof(sampling));
        }

        if (openStatus == PdfOpenStatus.Opened && (pageCount is null || sampling is null)
            || openStatus != PdfOpenStatus.Opened && sampling is not null
            || classification == PdfDocumentClassification.Encrypted
            && encryptionStatus is PdfEncryptionStatus.NotEncrypted or PdfEncryptionStatus.Unknown)
        {
            throw new ArgumentException("PDF open, encryption, classification, and sampling facts are contradictory.");
        }

        OpenStatus = openStatus;
        EncryptionStatus = encryptionStatus;
        PageCount = pageCount;
        PdfVersion = boundedVersion;
        Metadata = metadata;
        Text = text;
        Images = images;
        OutlinePresent = outlinePresent;
        OutlineEntryCount = outlineEntryCount;
        ActiveContent = activeContent;
        PageAnalysis = pageAnalysis;
        RepeatedPages = new ReadOnlyCollection<PdfRepeatedPageCluster>(repeats);
        Identifiers = new ReadOnlyCollection<PdfIdentifierEvidence>(identifierValues);
        Classification = classification;
        ClassificationConfidence = classificationConfidence;
        Sampling = sampling;
        ClassificationPolicyVersion = classificationPolicyVersion;
        ResourceProfileVersion = resourceProfileVersion;
        FactsIncomplete = factsIncomplete;
    }

    public PdfOpenStatus OpenStatus { get; }
    public PdfEncryptionStatus EncryptionStatus { get; }
    public int? PageCount { get; }
    public string? PdfVersion { get; }
    public PdfDocumentMetadataSummary Metadata { get; }
    public PdfTextSummary Text { get; }
    public PdfImageSummary Images { get; }
    public bool OutlinePresent { get; }
    public int OutlineEntryCount { get; }
    public PdfActiveContentSummary ActiveContent { get; }
    public PdfPageAnalysisSummary PageAnalysis { get; }
    public IReadOnlyList<PdfRepeatedPageCluster> RepeatedPages { get; }
    public IReadOnlyList<PdfIdentifierEvidence> Identifiers { get; }
    public PdfDocumentClassification Classification { get; }
    public PdfClassificationConfidence ClassificationConfidence { get; }
    public PdfSamplingSummary? Sampling { get; }
    public PdfPolicyVersion ClassificationPolicyVersion { get; }
    public PdfPolicyVersion ResourceProfileVersion { get; }
    public bool FactsIncomplete { get; }
}
