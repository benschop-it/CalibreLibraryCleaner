using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Domain.Plans;

public sealed record ExpectedAuthorState(CalibreAuthorId Id, string Name, string SortName);
public sealed record ExpectedIdentifierState(string Type, string Value);

public sealed record ExpectedFormatState
{
    public ExpectedFormatState(
        CalibreBookId recordId,
        string format,
        string storedFileName,
        string relativePath,
        FormatFileStatus status,
        FormatFileFingerprint fingerprint,
        FormatFileObservation observation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ValidateStoredFileName(storedFileName);
        ValidateRelativePath(relativePath);
        if (status != FormatFileStatus.Present) throw new ArgumentException("Affected cleanup-plan formats must be present.", nameof(status));
        if (fingerprint.SizeInBytes != observation.Length) throw new ArgumentException("Expected format length facts must agree.", nameof(observation));
        RecordId = recordId;
        Format = format.ToUpperInvariant();
        StoredFileName = storedFileName;
        RelativePath = Normalize(relativePath);
        Status = status;
        Fingerprint = fingerprint;
        Observation = observation;
        ObservationSourceVersion = FormatFileObservation.SourceVersion;
    }

    public CalibreBookId RecordId { get; }
    public string Format { get; }
    public string StoredFileName { get; }
    public string RelativePath { get; }
    public FormatFileStatus Status { get; }
    public FormatFileFingerprint Fingerprint { get; }
    public FormatFileObservation Observation { get; }
    public string ObservationSourceVersion { get; }

    public static void ValidateRelativePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = Normalize(value);
        if (normalized.StartsWith('/')
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':')
            || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Expected managed paths must be safe relative paths.", nameof(value));
        }
    }

    public static void ValidateStoredFileName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 255
            || value is "." or ".."
            || value.Contains('/')
            || value.Contains('\\')
            || value.Contains(':'))
            throw new ArgumentException("Expected stored format names must be safe filename stems.", nameof(value));
    }

    private static string Normalize(string value) => value.Replace('\\', '/');
}

public sealed record ExpectedRecordState
{
    public ExpectedRecordState(
        CalibreBookId recordId,
        string title,
        string authorSort,
        IEnumerable<ExpectedAuthorState> authors,
        IEnumerable<ExpectedIdentifierState> identifiers,
        string? publisher,
        DateTimeOffset? publicationDate,
        string? series,
        decimal? seriesIndex,
        IEnumerable<string> languages,
        bool hasCover,
        string relativeDirectory,
        IEnumerable<ExpectedFormatState> formats)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(authorSort);
        ArgumentNullException.ThrowIfNull(authors);
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(languages);
        ExpectedFormatState.ValidateRelativePath(relativeDirectory);
        ExpectedFormatState[] orderedFormats = formats.OrderBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.RelativePath, StringComparer.Ordinal).ToArray();
        if (orderedFormats.Any(value => value.RecordId != recordId)
            || orderedFormats.Select(value => (value.Format, value.RelativePath)).Distinct().Count() != orderedFormats.Length)
            throw new ArgumentException("Expected formats must be unique and owned by their record.", nameof(formats));
        RecordId = recordId;
        Title = title;
        AuthorSort = authorSort;
        Authors = Array.AsReadOnly(authors.Select(value => new ExpectedAuthorState(value.Id, value.Name, value.SortName)).ToArray());
        Identifiers = Array.AsReadOnly(identifiers.OrderBy(value => value.Type, StringComparer.Ordinal).ThenBy(value => value.Value, StringComparer.Ordinal).ToArray());
        Publisher = publisher;
        PublicationDate = publicationDate?.ToUniversalTime();
        Series = series;
        SeriesIndex = seriesIndex;
        Languages = Array.AsReadOnly(languages.ToArray());
        HasCover = hasCover;
        RelativeDirectory = relativeDirectory.Replace('\\', '/');
        Formats = Array.AsReadOnly(orderedFormats);
    }

    public CalibreBookId RecordId { get; }
    public string Title { get; }
    public string AuthorSort { get; }
    public IReadOnlyList<ExpectedAuthorState> Authors { get; }
    public IReadOnlyList<ExpectedIdentifierState> Identifiers { get; }
    public string? Publisher { get; }
    public DateTimeOffset? PublicationDate { get; }
    public string? Series { get; }
    public decimal? SeriesIndex { get; }
    public IReadOnlyList<string> Languages { get; }
    public bool HasCover { get; }
    public string RelativeDirectory { get; }
    public IReadOnlyList<ExpectedFormatState> Formats { get; }
}

public sealed record ExpectedLibraryState
{
    public ExpectedLibraryState(
        string libraryUuid,
        int schemaVersion,
        ExactMetadataDuplicateGroupId groupId,
        IEnumerable<CalibreBookId> memberIds,
        IEnumerable<ExpectedRecordState> records,
        RecommendationModelVersion recommendationModelVersion,
        RecommendationInputVersion recommendationInputVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryUuid);
        if (!Guid.TryParse(libraryUuid, out _)) throw new ArgumentException("The expected library UUID is invalid.", nameof(libraryUuid));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        CalibreBookId[] members = memberIds.Distinct().OrderBy(value => value.Value).ToArray();
        ExpectedRecordState[] orderedRecords = records.OrderBy(value => value.RecordId.Value).ToArray();
        if (members.Length < 2 || !members.SequenceEqual(orderedRecords.Select(value => value.RecordId)))
            throw new ArgumentException("Expected records must exactly match duplicate-group members.", nameof(records));
        LibraryUuid = libraryUuid;
        SchemaVersion = schemaVersion;
        GroupId = groupId;
        MemberIds = Array.AsReadOnly(members);
        Records = Array.AsReadOnly(orderedRecords);
        RecommendationModelVersion = recommendationModelVersion;
        RecommendationInputVersion = recommendationInputVersion;
    }

    public string LibraryUuid { get; }
    public int SchemaVersion { get; }
    public ExactMetadataDuplicateGroupId GroupId { get; }
    public IReadOnlyList<CalibreBookId> MemberIds { get; }
    public IReadOnlyList<ExpectedRecordState> Records { get; }
    public RecommendationModelVersion RecommendationModelVersion { get; }
    public RecommendationInputVersion RecommendationInputVersion { get; }
}
