using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Domain.Tests.Matching.Evaluation;

public sealed record MatchingCorpus(
    string SchemaVersion,
    string CorpusVersion,
    MatchingCorpusSplit Split,
    MatchingCorpusSource Source,
    IReadOnlyList<MatchingScenario> Scenarios);

[JsonConverter(typeof(JsonStringEnumConverter<MatchingCorpusSplit>))]
public enum MatchingCorpusSplit
{
    Calibration,
    Holdout,
}

public sealed record MatchingCorpusSource(
    string Id,
    string Provenance,
    string License);

public sealed record MatchingScenario(
    string Id,
    IReadOnlyList<string> Tags,
    string SourceFamilyId,
    string? GenerationTemplateId,
    string? GenerationTemplateVersion,
    IReadOnlyList<MatchingRecordFixture> Records,
    IReadOnlyList<MatchingContentOracle> ContentComparisons,
    IReadOnlyList<MatchingExpectedGroup> ExpectedGroups,
    string ReviewNote,
    int Repeat = 1,
    long CalibreIdStride = 0,
    IReadOnlyList<MatchingBibliographicResolution>? BibliographicResolutions = null);

public sealed record MatchingRecordFixture(
    string Key,
    long CalibreId,
    string Title,
    string AuthorSort,
    IReadOnlyList<MatchingAuthorFixture> Authors,
    IReadOnlyList<MatchingIdentifierFixture> Identifiers,
    MatchingPublicationFixture Publication,
    IReadOnlyList<MatchingFormatFixture> Formats,
    MatchingEpubFixture? Epub,
    string WorkKey,
    string ExpectedLanguage);

public sealed record MatchingAuthorFixture(string Name, string SortName);

public sealed record MatchingIdentifierFixture(string Type, string Value);

public sealed record MatchingPublicationFixture(
    string? Publisher,
    string? PublicationDate,
    string? Series,
    decimal? SeriesIndex,
    IReadOnlyList<string> Languages,
    bool HasCover);

public sealed record MatchingFormatFixture(
    string Format,
    string StoredFileName,
    string ExpectedRelativePath,
    string? Sha256,
    long? SizeInBytes);

public sealed record MatchingEpubFixture(
    string Status,
    int? Score,
    string AnalyzerVersion,
    string ScoringModelVersion,
    string ExpectedRelativePath,
    string? EmbeddedTitle,
    IReadOnlyList<string> Authors,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> StrongIdentifiers);

public sealed record MatchingContentOracle(
    string FirstRecordKey,
    string SecondRecordKey,
    string Classification,
    int ForwardStrictMatches,
    int ReverseStrictMatches,
    int ForwardRelaxedMatches,
    int ReverseRelaxedMatches,
    int MatchedRegionCount,
    int TokenCountRatioPermille,
    int ShingleSimilarityPermille);

public sealed record MatchingExpectedGroup(
    string WorkKey,
    string Language,
    IReadOnlyList<string> AcceptableKeeperRecordIds);

public sealed record MatchingBibliographicResolution(
    string RecordKey,
    string ProviderId,
    string ProviderVersion,
    BibliographicQueryFields QueryFields,
    string RetrievedAtUtc,
    BibliographicResolutionStatus Status,
    string? WorkId,
    string? ProblemCode);

public static class MatchingCorpusVocabulary
{
    public const string SchemaVersion = "matching-corpus/1.0";

    public static IReadOnlySet<string> Tags { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "author-variant",
        "broad-author-bucket",
        "candidate-cap",
        "content-ambiguous",
        "content-different",
        "content-equivalent",
        "content-high",
        "content-unavailable",
        "edition-variant",
        "exact-metadata",
        "format-diversity",
        "identifier-anchor",
        "identifier-conflict",
        "keeper-ranking",
        "language-conflict",
        "malformed-metadata",
        "metadata-only",
        "missing-language",
        "multi-record-chain",
        "pdf-only",
        "series-conflict",
        "same-title-different-work",
        "title-noise",
        "unicode-punctuation",
    };
}
