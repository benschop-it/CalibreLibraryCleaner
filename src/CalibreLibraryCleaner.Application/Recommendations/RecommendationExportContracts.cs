using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Application.Recommendations;

public sealed record RecommendationReviewExportDocument
{
    public const string CurrentSchemaVersion = "recommendation-review/1.0";

    public RecommendationReviewExportDocument(
        string sourceLibraryUuid,
        int sourceSchemaVersion,
        DateTimeOffset exportedAtUtc,
        IEnumerable<RecommendationReviewExportGroup> groups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLibraryUuid);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceSchemaVersion);
        ArgumentNullException.ThrowIfNull(groups);
        RecommendationReviewExportGroup[] ordered = groups
            .OrderBy(value => value.Reviewed.Generated.GroupId.Value, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Select(value => value.Reviewed.Generated.GroupId).Distinct().Count() != ordered.Length)
        {
            throw new ArgumentException("Export group identities must be unique.", nameof(groups));
        }

        SchemaVersion = CurrentSchemaVersion;
        RecommendationModelVersion = RecommendationModelVersion.V1;
        SourceLibraryUuid = sourceLibraryUuid;
        SourceSchemaVersion = sourceSchemaVersion;
        ExportedAtUtc = exportedAtUtc.ToUniversalTime();
        Groups = new ReadOnlyCollection<RecommendationReviewExportGroup>(ordered);
    }

    public string SchemaVersion { get; }
    public RecommendationModelVersion RecommendationModelVersion { get; }
    public string SourceLibraryUuid { get; }
    public int SourceSchemaVersion { get; }
    public DateTimeOffset ExportedAtUtc { get; }
    public IReadOnlyList<RecommendationReviewExportGroup> Groups { get; }
}

public sealed record RecommendationReviewExportGroup(
    ReviewedConsolidationRecommendation Reviewed,
    IReadOnlyList<CalibreBook> Members);

public sealed record RecommendationExportWriteOutcome(
    bool IsSuccess,
    string? PublishedPath,
    RecommendationExportError? Error)
{
    public static RecommendationExportWriteOutcome Success(string path) => new(true, path, null);
    public static RecommendationExportWriteOutcome Failure(string code, string message) => new(false, null, new(code, message));
}

public sealed record RecommendationExportError(string Code, string Message);
