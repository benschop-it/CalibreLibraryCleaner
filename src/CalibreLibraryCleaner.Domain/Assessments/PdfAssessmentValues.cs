namespace CalibreLibraryCleaner.Domain.Assessments;

public enum PdfDocumentClassification
{
    DigitalText,
    ScannedWithoutOcr,
    ScannedWithOcr,
    Mixed,
    EmptyOrNearEmpty,
    Encrypted,
    Unreadable,
    Unknown,
}

public enum PdfClassificationConfidence
{
    Insufficient,
    Low,
    Medium,
    High,
}

public enum PdfOpenStatus
{
    Opened,
    Unreadable,
    ResourceLimited,
    TimedOut,
    WorkerFailed,
    Unknown,
}

public enum PdfEncryptionStatus
{
    NotEncrypted,
    EncryptedAccessibleWithEmptyPassword,
    PasswordRequired,
    UnsupportedEncryption,
    Unknown,
}

public enum PdfSamplingMode
{
    AllPages,
    DeterministicSample,
}

public enum PdfTextExtractionStatus
{
    Available,
    Unavailable,
    Partial,
    Unknown,
}

public enum PdfTextDensityBand
{
    None,
    Sparse,
    Moderate,
    Useful,
    Unknown,
}

public enum PdfContentBalance
{
    TextHeavy,
    ImageHeavy,
    Balanced,
    NoObservableContent,
    Unknown,
}

public enum PdfRepeatedPageEvidence
{
    ExactSharedPageContent,
    ExactNormalizedText,
    LikelySharedImageContent,
    Insufficient,
}

public enum PdfMetadataValueStatus
{
    Present,
    Missing,
    Malformed,
    Truncated,
}

public enum PdfIdentifierSource
{
    DocumentInformation,
    XmpMetadata,
    EarlyPageText,
}

public sealed record PdfPolicyVersion
{
    public PdfPolicyVersion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record PdfMetadataValue
{
    public PdfMetadataValue(PdfMetadataValueStatus status, string? value = null)
    {
        string? bounded = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (bounded is { Length: > 512 })
        {
            throw new ArgumentException("PDF metadata values are limited to 512 characters.", nameof(value));
        }

        if (status == PdfMetadataValueStatus.Present && bounded is null)
        {
            throw new ArgumentException("Present PDF metadata requires a value.", nameof(value));
        }

        if (status == PdfMetadataValueStatus.Missing && bounded is not null)
        {
            throw new ArgumentException("Missing PDF metadata cannot retain a value.", nameof(value));
        }

        Status = status;
        Value = bounded;
    }

    public PdfMetadataValueStatus Status { get; }
    public string? Value { get; }
}

public sealed record PdfDocumentMetadataSummary(
    PdfMetadataValue Title,
    PdfMetadataValue Author,
    PdfMetadataValue Subject,
    PdfMetadataValue Keywords,
    PdfMetadataValue Creator,
    PdfMetadataValue Producer,
    PdfMetadataValue CreationDate,
    PdfMetadataValue ModificationDate)
{
    public static PdfDocumentMetadataSummary Empty { get; } = new(
        new(PdfMetadataValueStatus.Missing),
        new(PdfMetadataValueStatus.Missing),
        new(PdfMetadataValueStatus.Missing),
        new(PdfMetadataValueStatus.Missing),
        new(PdfMetadataValueStatus.Missing),
        new(PdfMetadataValueStatus.Missing),
        new(PdfMetadataValueStatus.Missing),
        new(PdfMetadataValueStatus.Missing));
}

public sealed record PdfIdentifierEvidence
{
    public PdfIdentifierEvidence(string normalizedIsbn, PdfIdentifierSource source, int? pageNumber = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedIsbn);
        if (!HasValidChecksum(normalizedIsbn))
        {
            throw new ArgumentException("Identifier evidence must contain a checksum-valid normalized ISBN-10 or ISBN-13.", nameof(normalizedIsbn));
        }

        if ((source == PdfIdentifierSource.EarlyPageText) != pageNumber.HasValue || pageNumber <= 0)
        {
            throw new ArgumentException("Only early-page evidence carries a positive page number.", nameof(pageNumber));
        }

        NormalizedIsbn = normalizedIsbn;
        Source = source;
        PageNumber = pageNumber;
    }

    public string NormalizedIsbn { get; }
    public PdfIdentifierSource Source { get; }
    public int? PageNumber { get; }

    private static bool HasValidChecksum(string value)
    {
        if (value.Length == 13
            && (value.StartsWith("978", StringComparison.Ordinal) || value.StartsWith("979", StringComparison.Ordinal))
            && value.All(char.IsAsciiDigit))
        {
            int sum = value.Take(12).Select((character, index) => (character - '0') * (index % 2 == 0 ? 1 : 3)).Sum();
            return (10 - sum % 10) % 10 == value[12] - '0';
        }

        if (value.Length != 10
            || value.Take(9).Any(character => !char.IsAsciiDigit(character))
            || (!char.IsAsciiDigit(value[9]) && value[9] != 'X'))
        {
            return false;
        }

        int weighted = value.Take(9).Select((character, index) => (character - '0') * (10 - index)).Sum();
        weighted += value[9] == 'X' ? 10 : value[9] - '0';
        return weighted % 11 == 0;
    }
}

public sealed record PdfRepeatedPageCluster
{
    public PdfRepeatedPageCluster(PdfRepeatedPageEvidence evidence, IEnumerable<int> pageNumbers)
    {
        ArgumentNullException.ThrowIfNull(pageNumbers);
        int[] pages = pageNumbers.Distinct().Order().ToArray();
        if (pages.Length < 2 || pages.Length > 20 || pages.Any(page => page <= 0))
        {
            throw new ArgumentException("A repeated-page cluster requires 2 through 20 distinct positive page numbers.", nameof(pageNumbers));
        }

        Evidence = evidence;
        PageNumbers = Array.AsReadOnly(pages);
    }

    public PdfRepeatedPageEvidence Evidence { get; }
    public IReadOnlyList<int> PageNumbers { get; }
}

public sealed record PdfScoreBreakdown(
    int TechnicalRawContribution,
    int TechnicalScore,
    int EmbeddedMetadataRawContribution,
    int EmbeddedMetadataScore)
{
    public int Total => TechnicalScore + EmbeddedMetadataScore;
}
