using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Domain.Tests.Matching.Evaluation;

public sealed class MatchingCorpusValidationException : Exception
{
    public MatchingCorpusValidationException(string code, string? scenarioId = null, string? recordId = null)
        : base(Format(code, scenarioId, recordId)) => Code = code;

    public string Code { get; }

    private static string Format(string code, string? scenarioId, string? recordId)
    {
        List<string> parts = [code];
        if (MatchingCorpusLoader.IsOpaqueId(scenarioId)) parts.Add($"scenario={scenarioId}");
        if (MatchingCorpusLoader.IsOpaqueId(recordId)) parts.Add($"record={recordId}");
        return string.Join(';', parts);
    }
}

public static class MatchingCorpusLoader
{
    public const int MaximumDocumentBytes = 32 * 1024 * 1024;
    public const int MaximumScenarios = 10_000;
    public const int MaximumTotalRecords = 100_000;
    public const int MaximumRecordsPerScenario = 100;
    public const int MaximumStringLength = 512;
    public const int MaximumValuesPerRecordField = 64;
    public const int MaximumTagsPerScenario = 32;
    public const int MaximumComparisonsPerScenario = 4_950;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() },
    };

    public static MatchingCorpus Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes = ReadBounded(stream);
        MatchingCorpus corpus;
        try
        {
            corpus = JsonSerializer.Deserialize<MatchingCorpus>(bytes, Options)
                ?? throw new MatchingCorpusValidationException("CORPUS.JSON_NULL");
        }
        catch (MatchingCorpusValidationException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new MatchingCorpusValidationException("CORPUS.JSON_INVALID");
        }

        MatchingCorpus expanded = ExpandTemplates(corpus);
        Validate(expanded);
        return expanded;
    }

    public static MatchingCorpus LoadExternal(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumDocumentBytes)
                throw new MatchingCorpusValidationException("CORPUS.DOCUMENT_TOO_LARGE");
            return Load(stream);
        }
        catch (MatchingCorpusValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MatchingCorpusValidationException("CORPUS.FILE_READ_FAILED");
        }
    }

    public static void ValidateSplits(MatchingCorpus calibration, MatchingCorpus holdout)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(holdout);
        if (calibration.Split != MatchingCorpusSplit.Calibration
            || holdout.Split != MatchingCorpusSplit.Holdout)
            throw new MatchingCorpusValidationException("CORPUS.SPLIT_PAIR_INVALID");
        if (!string.Equals(calibration.SchemaVersion, holdout.SchemaVersion, StringComparison.Ordinal))
            throw new MatchingCorpusValidationException("CORPUS.SCHEMA_MIXED");

        HashSet<string> calibrationWorks = calibration.Scenarios.SelectMany(value => value.Records)
            .Select(value => value.WorkKey).ToHashSet(StringComparer.Ordinal);
        if (holdout.Scenarios.SelectMany(value => value.Records)
            .Any(value => calibrationWorks.Contains(value.WorkKey)))
            throw new MatchingCorpusValidationException("CORPUS.WORK_SPLIT_LEAKAGE");

        HashSet<string> calibrationSources = calibration.Scenarios.Select(value => value.SourceFamilyId)
            .ToHashSet(StringComparer.Ordinal);
        if (holdout.Scenarios.Any(value => calibrationSources.Contains(value.SourceFamilyId)))
            throw new MatchingCorpusValidationException("CORPUS.SOURCE_SPLIT_LEAKAGE");
    }

    public static string CanonicalDigest(MatchingCorpus corpus)
    {
        Validate(corpus);
        MatchingCorpus canonical = corpus with
        {
            Scenarios = corpus.Scenarios.OrderBy(value => value.Id, StringComparer.Ordinal)
                .Select(Canonicalize).ToArray(),
        };
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, Options)));
    }

    internal static bool IsOpaqueId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static byte[] ReadBounded(Stream stream)
    {
        using MemoryStream buffer = new();
        byte[] block = new byte[81920];
        int read;
        while ((read = stream.Read(block, 0, block.Length)) > 0)
        {
            if (buffer.Length + read > MaximumDocumentBytes)
                throw new MatchingCorpusValidationException("CORPUS.DOCUMENT_TOO_LARGE");
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    private static void Validate(MatchingCorpus corpus)
    {
        if (!string.Equals(corpus.SchemaVersion, MatchingCorpusVocabulary.SchemaVersion, StringComparison.Ordinal))
            throw new MatchingCorpusValidationException("CORPUS.SCHEMA_UNSUPPORTED");
        RequireText(corpus.CorpusVersion, "CORPUS.VERSION_INVALID");
        if (!Enum.IsDefined(corpus.Split)) throw new MatchingCorpusValidationException("CORPUS.SPLIT_INVALID");
        ValidateSource(corpus.Source);
        if (corpus.Scenarios is null || corpus.Scenarios.Count is 0 or > MaximumScenarios)
            throw new MatchingCorpusValidationException("CORPUS.SCENARIO_COUNT_INVALID");
        if (corpus.Scenarios.Sum(value => value.Records?.Count ?? 0) > MaximumTotalRecords)
            throw new MatchingCorpusValidationException("CORPUS.RECORD_LIMIT_EXCEEDED");

        HashSet<string> scenarioIds = new(StringComparer.Ordinal);
        foreach (MatchingScenario scenario in corpus.Scenarios)
        {
            if (!IsOpaqueId(scenario.Id) || !scenarioIds.Add(scenario.Id))
                throw new MatchingCorpusValidationException("CORPUS.SCENARIO_ID_INVALID", scenario.Id);
            ValidateScenario(scenario);
        }
    }

    private static void ValidateSource(MatchingCorpusSource? source)
    {
        if (source is null || !IsOpaqueId(source.Id))
            throw new MatchingCorpusValidationException("CORPUS.SOURCE_INVALID");
        RequireText(source.Provenance, "CORPUS.PROVENANCE_INVALID");
        RequireText(source.License, "CORPUS.LICENSE_INVALID");
    }

    private static void ValidateScenario(MatchingScenario scenario)
    {
        RequireText(scenario.SourceFamilyId, "CORPUS.SOURCE_FAMILY_INVALID", scenario.Id);
        RequireOptionalText(scenario.GenerationTemplateId, "CORPUS.TEMPLATE_INVALID", scenario.Id);
        RequireOptionalText(scenario.GenerationTemplateVersion, "CORPUS.TEMPLATE_VERSION_INVALID", scenario.Id);
        RequireText(scenario.ReviewNote, "CORPUS.REVIEW_NOTE_INVALID", scenario.Id);
        if (scenario.Repeat != 1 || scenario.CalibreIdStride != 0)
            throw new MatchingCorpusValidationException("CORPUS.TEMPLATE_NOT_EXPANDED", scenario.Id);
        if (scenario.Tags is null || scenario.Tags.Count is 0 or > MaximumTagsPerScenario
            || scenario.Tags.Distinct(StringComparer.Ordinal).Count() != scenario.Tags.Count
            || scenario.Tags.Any(value => !MatchingCorpusVocabulary.Tags.Contains(value)))
            throw new MatchingCorpusValidationException("CORPUS.TAGS_INVALID", scenario.Id);
        if (scenario.Records is null || scenario.Records.Count is < 2 or > MaximumRecordsPerScenario)
            throw new MatchingCorpusValidationException("CORPUS.SCENARIO_RECORD_COUNT_INVALID", scenario.Id);
        if (scenario.ContentComparisons is null
            || scenario.ContentComparisons.Count > MaximumComparisonsPerScenario)
            throw new MatchingCorpusValidationException("CORPUS.CONTENT_COUNT_INVALID", scenario.Id);
        if (scenario.ExpectedGroups is null)
            throw new MatchingCorpusValidationException("CORPUS.EXPECTED_GROUPS_MISSING", scenario.Id);

        HashSet<string> keys = new(StringComparer.Ordinal);
        HashSet<long> calibreIds = [];
        foreach (MatchingRecordFixture record in scenario.Records)
        {
            if (!IsOpaqueId(record.Key) || !keys.Add(record.Key) || record.CalibreId <= 0
                || !calibreIds.Add(record.CalibreId))
                throw new MatchingCorpusValidationException("CORPUS.RECORD_ID_INVALID", scenario.Id, record.Key);
            ValidateRecord(scenario.Id, record);
        }
        ValidateExpectedGroups(scenario, keys);
        ValidateComparisons(scenario, keys);
    }

    private static void ValidateRecord(string scenarioId, MatchingRecordFixture record)
    {
        RequireText(record.Title, "CORPUS.TITLE_INVALID", scenarioId, record.Key);
        RequireText(record.AuthorSort, "CORPUS.AUTHOR_SORT_INVALID", scenarioId, record.Key, allowEmpty: true);
        RequireText(record.WorkKey, "CORPUS.WORK_KEY_INVALID", scenarioId, record.Key);
        RequireText(record.ExpectedLanguage, "CORPUS.LANGUAGE_INVALID", scenarioId, record.Key);
        if (record.ExpectedLanguage != "und"
            && !string.Equals(CandidateMetadataNormalizer.NormalizeLanguage(record.ExpectedLanguage),
                record.ExpectedLanguage, StringComparison.Ordinal))
            throw new MatchingCorpusValidationException("CORPUS.LANGUAGE_INVALID", scenarioId, record.Key);
        ValidateBoundedList(record.Authors, MaximumValuesPerRecordField, "CORPUS.AUTHORS_INVALID", scenarioId, record.Key);
        foreach (MatchingAuthorFixture author in record.Authors)
        {
            RequireText(author.Name, "CORPUS.AUTHOR_INVALID", scenarioId, record.Key);
            RequireText(author.SortName, "CORPUS.AUTHOR_INVALID", scenarioId, record.Key, allowEmpty: true);
        }
        ValidateBoundedList(record.Identifiers, MaximumValuesPerRecordField, "CORPUS.IDENTIFIERS_INVALID", scenarioId, record.Key);
        foreach (MatchingIdentifierFixture identifier in record.Identifiers)
        {
            RequireText(identifier.Type, "CORPUS.IDENTIFIER_INVALID", scenarioId, record.Key);
            RequireText(identifier.Value, "CORPUS.IDENTIFIER_INVALID", scenarioId, record.Key);
        }
        ValidatePublication(record.Publication, scenarioId, record.Key);
        ValidateFormats(record.Formats, scenarioId, record.Key);
        if (record.Epub is not null) ValidateEpub(record.Epub, scenarioId, record.Key);
    }

    private static void ValidatePublication(MatchingPublicationFixture? publication, string scenarioId, string recordId)
    {
        if (publication is null)
            throw new MatchingCorpusValidationException("CORPUS.PUBLICATION_MISSING", scenarioId, recordId);
        RequireOptionalText(publication.Publisher, "CORPUS.PUBLISHER_INVALID", scenarioId, recordId);
        RequireOptionalText(publication.Series, "CORPUS.SERIES_INVALID", scenarioId, recordId);
        if (publication.PublicationDate is not null
            && !DateTimeOffset.TryParse(publication.PublicationDate, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out _))
            throw new MatchingCorpusValidationException("CORPUS.PUBLICATION_DATE_INVALID", scenarioId, recordId);
        ValidateStringList(publication.Languages, "CORPUS.PUBLICATION_LANGUAGES_INVALID", scenarioId, recordId);
    }

    private static void ValidateFormats(IReadOnlyList<MatchingFormatFixture>? formats, string scenarioId, string recordId)
    {
        if (formats is null)
            throw new MatchingCorpusValidationException("CORPUS.FORMATS_INVALID", scenarioId, recordId);
        ValidateBoundedList(formats, MaximumValuesPerRecordField, "CORPUS.FORMATS_INVALID", scenarioId, recordId);
        foreach (MatchingFormatFixture format in formats)
        {
            RequireText(format.Format, "CORPUS.FORMAT_INVALID", scenarioId, recordId);
            RequireText(format.StoredFileName, "CORPUS.FORMAT_INVALID", scenarioId, recordId, allowEmpty: true);
            RequireRelativePath(format.ExpectedRelativePath, scenarioId, recordId);
            if ((format.Sha256 is null) != (format.SizeInBytes is null)
                || format.SizeInBytes < 0
                || format.Sha256 is not null && (format.Sha256.Length != 64 || !format.Sha256.All(Uri.IsHexDigit)))
                throw new MatchingCorpusValidationException("CORPUS.FINGERPRINT_INVALID", scenarioId, recordId);
        }
    }

    private static void ValidateEpub(MatchingEpubFixture epub, string scenarioId, string recordId)
    {
        RequireText(epub.Status, "CORPUS.ASSESSMENT_STATUS_INVALID", scenarioId, recordId);
        if (epub.Status is not ("Completed" or "Unassessed" or "Disqualified")
            || epub.Score is < 0 or > 100
            || epub.Status == "Completed" && epub.Score is null
            || epub.Status != "Completed" && epub.Score is not null)
            throw new MatchingCorpusValidationException("CORPUS.ASSESSMENT_INVALID", scenarioId, recordId);
        RequireText(epub.AnalyzerVersion, "CORPUS.ANALYZER_VERSION_INVALID", scenarioId, recordId);
        RequireText(epub.ScoringModelVersion, "CORPUS.SCORING_VERSION_INVALID", scenarioId, recordId);
        RequireRelativePath(epub.ExpectedRelativePath, scenarioId, recordId);
        RequireOptionalText(epub.EmbeddedTitle, "CORPUS.EMBEDDED_TITLE_INVALID", scenarioId, recordId);
        ValidateStringList(epub.Authors, "CORPUS.EMBEDDED_AUTHORS_INVALID", scenarioId, recordId);
        ValidateStringList(epub.Languages, "CORPUS.EMBEDDED_LANGUAGES_INVALID", scenarioId, recordId);
        ValidateStringList(epub.StrongIdentifiers, "CORPUS.EMBEDDED_IDENTIFIERS_INVALID", scenarioId, recordId);
    }

    private static void ValidateExpectedGroups(MatchingScenario scenario, HashSet<string> keys)
    {
        Dictionary<(string Work, string Language), string[]> actual = scenario.Records
            .GroupBy(value => (value.WorkKey, value.ExpectedLanguage))
            .Where(group => group.Count() >= 2)
            .ToDictionary(group => group.Key, group => group.Select(value => value.Key).ToArray());
        if (scenario.ExpectedGroups.Count != actual.Count)
            throw new MatchingCorpusValidationException("CORPUS.EXPECTED_GROUP_COUNT_INVALID", scenario.Id);
        HashSet<(string Work, string Language)> seen = [];
        foreach (MatchingExpectedGroup group in scenario.ExpectedGroups)
        {
            RequireText(group.WorkKey, "CORPUS.EXPECTED_GROUP_INVALID", scenario.Id);
            RequireText(group.Language, "CORPUS.EXPECTED_GROUP_INVALID", scenario.Id);
            if (!seen.Add((group.WorkKey, group.Language))
                || !actual.TryGetValue((group.WorkKey, group.Language), out string[]? members)
                || group.AcceptableKeeperRecordIds is null or { Count: 0 }
                || group.AcceptableKeeperRecordIds.Distinct(StringComparer.Ordinal).Count()
                    != group.AcceptableKeeperRecordIds.Count
                || group.AcceptableKeeperRecordIds.Any(value => !keys.Contains(value) || !members.Contains(value)))
                throw new MatchingCorpusValidationException("CORPUS.EXPECTED_GROUP_INVALID", scenario.Id);
        }
    }

    private static void ValidateComparisons(MatchingScenario scenario, HashSet<string> keys)
    {
        HashSet<string> pairs = new(StringComparer.Ordinal);
        foreach (MatchingContentOracle comparison in scenario.ContentComparisons)
        {
            string pair = $"{comparison.FirstRecordKey}|{comparison.SecondRecordKey}";
            if (!keys.Contains(comparison.FirstRecordKey) || !keys.Contains(comparison.SecondRecordKey)
                || string.CompareOrdinal(comparison.FirstRecordKey, comparison.SecondRecordKey) >= 0
                || !pairs.Add(pair)
                || !Enum.TryParse(comparison.Classification, ignoreCase: false,
                    out ContentSimilarityClassification classification))
                throw new MatchingCorpusValidationException("CORPUS.CONTENT_PAIR_INVALID", scenario.Id);
            try
            {
                _ = new CandidateContentComparison(
                    classification,
                    comparison.ForwardStrictMatches,
                    comparison.ReverseStrictMatches,
                    comparison.ForwardRelaxedMatches,
                    comparison.ReverseRelaxedMatches,
                    comparison.MatchedRegionCount,
                    comparison.TokenCountRatioPermille,
                    comparison.ShingleSimilarityPermille);
            }
            catch (ArgumentException)
            {
                throw new MatchingCorpusValidationException("CORPUS.CONTENT_EVIDENCE_INVALID", scenario.Id);
            }
        }
    }

    private static void ValidateStringList(
        IReadOnlyList<string>? values,
        string code,
        string scenarioId,
        string recordId)
    {
        if (values is null)
            throw new MatchingCorpusValidationException(code, scenarioId, recordId);
        ValidateBoundedList(values, MaximumValuesPerRecordField, code, scenarioId, recordId);
        if (values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > MaximumStringLength))
            throw new MatchingCorpusValidationException(code, scenarioId, recordId);
    }

    private static void ValidateBoundedList<T>(
        IReadOnlyList<T>? values,
        int maximum,
        string code,
        string scenarioId,
        string recordId)
    {
        if (values is null || values.Count > maximum)
            throw new MatchingCorpusValidationException(code, scenarioId, recordId);
    }

    private static void RequireRelativePath(string? value, string scenarioId, string recordId)
    {
        if (value is null)
            throw new MatchingCorpusValidationException("CORPUS.PATH_INVALID", scenarioId, recordId);
        RequireText(value, "CORPUS.PATH_INVALID", scenarioId, recordId);
        if (Path.IsPathRooted(value) || value.Contains('\\')
            || value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new MatchingCorpusValidationException("CORPUS.PATH_INVALID", scenarioId, recordId);
    }

    private static void RequireOptionalText(
        string? value,
        string code,
        string? scenarioId = null,
        string? recordId = null)
    {
        if (value is not null) RequireText(value, code, scenarioId, recordId);
    }

    private static void RequireText(
        string? value,
        string code,
        string? scenarioId = null,
        string? recordId = null,
        bool allowEmpty = false)
    {
        if (value is null || value.Length > MaximumStringLength || !allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new MatchingCorpusValidationException(code, scenarioId, recordId);
    }

    private static MatchingScenario Canonicalize(MatchingScenario scenario) => scenario with
    {
        Tags = scenario.Tags.Order(StringComparer.Ordinal).ToArray(),
        Records = scenario.Records.OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(Canonicalize).ToArray(),
        ContentComparisons = scenario.ContentComparisons
            .OrderBy(value => value.FirstRecordKey, StringComparer.Ordinal)
            .ThenBy(value => value.SecondRecordKey, StringComparer.Ordinal).ToArray(),
        ExpectedGroups = scenario.ExpectedGroups.OrderBy(value => value.WorkKey, StringComparer.Ordinal)
            .ThenBy(value => value.Language, StringComparer.Ordinal)
            .Select(value => value with
            {
                AcceptableKeeperRecordIds = value.AcceptableKeeperRecordIds.Order(StringComparer.Ordinal).ToArray(),
            }).ToArray(),
    };

    private static MatchingCorpus ExpandTemplates(MatchingCorpus corpus)
    {
        if (corpus.Scenarios is null) return corpus;
        if (corpus.Scenarios.Count > MaximumScenarios)
            throw new MatchingCorpusValidationException("CORPUS.SCENARIO_COUNT_INVALID");
        List<MatchingScenario> scenarios = [];
        int expandedRecordCount = 0;
        foreach (MatchingScenario scenario in corpus.Scenarios)
        {
            if (scenario.Repeat is < 1 or > 100)
                throw new MatchingCorpusValidationException("CORPUS.TEMPLATE_REPEAT_INVALID", scenario.Id);
            int recordCount = scenario.Records?.Count ?? 0;
            if (scenario.Repeat > MaximumScenarios - scenarios.Count
                || recordCount > 0 && scenario.Repeat > (MaximumTotalRecords - expandedRecordCount) / recordCount)
                throw new MatchingCorpusValidationException("CORPUS.TEMPLATE_EXPANSION_LIMIT", scenario.Id);
            expandedRecordCount += checked(scenario.Repeat * recordCount);
            if (scenario.Repeat == 1)
            {
                scenarios.Add(scenario with { Repeat = 1, CalibreIdStride = 0 });
                continue;
            }
            if (scenario.CalibreIdStride <= 0
                || string.IsNullOrWhiteSpace(scenario.GenerationTemplateId)
                || string.IsNullOrWhiteSpace(scenario.GenerationTemplateVersion)
                || scenario.Records is null)
                throw new MatchingCorpusValidationException("CORPUS.TEMPLATE_INVALID", scenario.Id);

            for (int index = 0; index < scenario.Repeat; index++)
            {
                string suffix = $"g{index + 1:D2}";
                MatchingRecordFixture[] records;
                try
                {
                    records = scenario.Records.Select(record => record with
                    {
                        CalibreId = checked(record.CalibreId + index * scenario.CalibreIdStride),
                        WorkKey = $"{record.WorkKey}-{suffix}",
                    }).ToArray();
                }
                catch (OverflowException)
                {
                    throw new MatchingCorpusValidationException("CORPUS.TEMPLATE_ID_OVERFLOW", scenario.Id);
                }
                scenarios.Add(scenario with
                {
                    Id = $"{scenario.Id}-{suffix}",
                    SourceFamilyId = $"{scenario.SourceFamilyId}-{suffix}",
                    Records = records,
                    ExpectedGroups = scenario.ExpectedGroups.Select(group => group with
                    {
                        WorkKey = $"{group.WorkKey}-{suffix}",
                    }).ToArray(),
                    Repeat = 1,
                    CalibreIdStride = 0,
                });
            }
        }
        return corpus with { Scenarios = scenarios };
    }

    private static MatchingRecordFixture Canonicalize(MatchingRecordFixture record) => record with
    {
        Authors = record.Authors.OrderBy(value => value.Name, StringComparer.Ordinal)
            .ThenBy(value => value.SortName, StringComparer.Ordinal).ToArray(),
        Identifiers = record.Identifiers.OrderBy(value => value.Type, StringComparer.Ordinal)
            .ThenBy(value => value.Value, StringComparer.Ordinal).ToArray(),
        Publication = record.Publication with
        {
            Languages = record.Publication.Languages.Order(StringComparer.Ordinal).ToArray(),
        },
        Formats = record.Formats.OrderBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal).ToArray(),
        Epub = record.Epub is null ? null : record.Epub with
        {
            Authors = record.Epub.Authors.Order(StringComparer.Ordinal).ToArray(),
            Languages = record.Epub.Languages.Order(StringComparer.Ordinal).ToArray(),
            StrongIdentifiers = record.Epub.StrongIdentifiers.Order(StringComparer.Ordinal).ToArray(),
        },
    };
}
