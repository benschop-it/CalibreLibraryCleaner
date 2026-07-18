using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Infrastructure.Recommendations;

public static class RecommendationJsonSerializer
{
    public static byte[] Serialize(RecommendationReviewExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateDocument(document);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schemaVersion", document.SchemaVersion);
            writer.WriteString("recommendationModelVersion", document.RecommendationModelVersion.Value);
            writer.WritePropertyName("sourceLibrary");
            writer.WriteStartObject();
            writer.WriteString("uuid", document.SourceLibraryUuid);
            writer.WriteNumber("schemaVersion", document.SourceSchemaVersion);
            writer.WriteEndObject();
            writer.WriteString("exportedAtUtc", document.ExportedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WritePropertyName("groups");
            writer.WriteStartArray();
            foreach (RecommendationReviewExportGroup group in document.Groups)
            {
                WriteGroup(writer, group);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        byte[] json = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal));
        byte[] result = new byte[json.Length + 1];
        json.CopyTo(result, 0);
        result[^1] = (byte)'\n';
        return result;
    }

    public static RecommendationJsonReadOutcome Inspect(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            JsonElement root = document.RootElement;
            string? schema = RequiredString(root, "schemaVersion");
            if (schema != RecommendationReviewExportDocument.CurrentSchemaVersion)
            {
                return RecommendationJsonReadOutcome.Failure("UNSUPPORTED_SCHEMA_VERSION", "The recommendation-review schema version is not supported.");
            }

            string? model = RequiredString(root, "recommendationModelVersion");
            if (model != RecommendationModelVersion.V1.Value)
            {
                return RecommendationJsonReadOutcome.Failure("UNSUPPORTED_MODEL_VERSION", "The recommendation model version is not supported.");
            }

            JsonElement sourceLibrary = root.GetProperty("sourceLibrary");
            if (RequiredString(sourceLibrary, "uuid") is null
                || !sourceLibrary.TryGetProperty("schemaVersion", out JsonElement sourceSchema)
                || !sourceSchema.TryGetInt32(out int schemaVersion)
                || schemaVersion <= 0
                || RequiredString(root, "exportedAtUtc") is not { } exportedAt
                || !DateTimeOffset.TryParse(exportedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _))
            {
                return RecommendationJsonReadOutcome.Failure("INVALID_SOURCE", "The source-library identity or export timestamp is invalid.");
            }

            JsonElement groups = root.GetProperty("groups");
            if (groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() > 100_000)
            {
                return RecommendationJsonReadOutcome.Failure("INVALID_GROUPS", "The groups collection is invalid or exceeds its bound.");
            }

            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (JsonElement group in groups.EnumerateArray())
            {
                string? id = RequiredString(group, "duplicateGroupId");
                if (id is null || !ids.Add(id))
                {
                    return RecommendationJsonReadOutcome.Failure("INVALID_GROUP_ID", "Recommendation group identities must be nonblank and unique.");
                }

                if (RequiredString(group, "recommendationInputVersion") is null
                    || !group.TryGetProperty("members", out JsonElement membersElement)
                    || membersElement.ValueKind != JsonValueKind.Array
                    || membersElement.GetArrayLength() is < 2 or > 100_000)
                {
                    return RecommendationJsonReadOutcome.Failure("INVALID_MEMBERS", "Recommendation members are invalid or exceed their bound.");
                }

                HashSet<long> memberIds = [];
                Dictionary<(long RecordId, string Format, string Path), FormatFileStatus> memberCandidates = [];
                foreach (JsonElement member in membersElement.EnumerateArray())
                {
                    if (!member.TryGetProperty("recordId", out JsonElement recordIdElement)
                        || !recordIdElement.TryGetInt64(out long recordId)
                        || recordId <= 0
                        || !memberIds.Add(recordId)
                        || RequiredString(member, "title") is null
                        || !member.TryGetProperty("formats", out JsonElement formats)
                        || formats.ValueKind != JsonValueKind.Array
                        || formats.GetArrayLength() > 10_000)
                    {
                        return RecommendationJsonReadOutcome.Failure("INVALID_MEMBER", "A recommendation member or its format collection is invalid.");
                    }

                    HashSet<string> memberFormats = new(StringComparer.Ordinal);
                    foreach (JsonElement format in formats.EnumerateArray())
                    {
                        string? canonicalFormat = RequiredString(format, "format")?.ToUpperInvariant();
                        string? path = RequiredString(format, "relativePath");
                        string? fileStatusText = RequiredString(format, "fileStatus");
                        if (canonicalFormat is null || path is null || !memberFormats.Add(canonicalFormat) || !IsSafeRelativePath(path)
                            || !ValidEnum<FormatFileStatus>(fileStatusText)
                            || !memberCandidates.TryAdd((recordId, canonicalFormat, path), Enum.Parse<FormatFileStatus>(fileStatusText!)))
                        {
                            return RecommendationJsonReadOutcome.Failure("UNSAFE_RELATIVE_PATH", "The artifact contains an unsafe managed relative path.");
                        }
                    }
                }

                JsonElement generated = group.GetProperty("generatedRecommendation");
                if (RequiredString(generated, "modelVersion") != RecommendationModelVersion.V1.Value
                    || !generated.TryGetProperty("formatSelections", out JsonElement selections)
                    || selections.ValueKind != JsonValueKind.Array
                    || !ValidEnum<RecommendationConfidence>(RequiredString(group, "confidence"))
                    || !ValidEnum<RecommendationReviewStatus>(RequiredString(group, "reviewStatus"))
                    || !ValidEnum<RecommendationFreshness>(RequiredString(group, "freshness")))
                {
                    return RecommendationJsonReadOutcome.Failure("INVALID_RECOMMENDATION", "The generated recommendation or review classifications are invalid.");
                }

                HashSet<string> selectedFormats = new(StringComparer.Ordinal);
                Dictionary<string, HashSet<long>> presentSelectionSources = new(StringComparer.Ordinal);
                foreach (JsonElement selection in selections.EnumerateArray())
                {
                    if (!ValidateSelectionJson(selection, memberCandidates, out string? format)
                        || !selectedFormats.Add(format!))
                    {
                        return RecommendationJsonReadOutcome.Failure("INVALID_FORMAT_SELECTION", "A generated format selection is invalid.");
                    }

                    presentSelectionSources.Add(format!, selection.GetProperty("candidates")
                        .EnumerateArray()
                        .Where(candidate => RequiredString(candidate, "fileStatus") == FormatFileStatus.Present.ToString())
                        .Select(candidate => candidate.GetProperty("recordId").GetInt64())
                        .ToHashSet());
                }

                if (!ValidateOptionalMemberId(generated, "metadataSourceRecordId", memberIds)
                    || !ValidateMemberIdArray(generated, "proposedRedundantRecords", memberIds, out HashSet<long> redundant)
                    || !ValidateMemberIdArray(generated, "retainedSeparateRecords", memberIds, out HashSet<long> separate)
                    || redundant.Overlaps(separate))
                {
                    return RecommendationJsonReadOutcome.Failure("INVALID_RECORD_SELECTION", "Generated metadata or record selections are invalid.");
                }

                JsonElement currentOverride = group.GetProperty("userOverride");
                JsonElement staleOverride = group.GetProperty("staleOverride");
                JsonElement effective = group.GetProperty("effectiveFinalSelection");
                RecommendationFreshness freshness = Enum.Parse<RecommendationFreshness>(RequiredString(group, "freshness")!);
                string groupInputVersion = RequiredString(group, "recommendationInputVersion")!;
                if (!ValidateOverrideJson(currentOverride, memberIds, presentSelectionSources, model!, groupInputVersion, requireCurrentAssociations: true)
                    || !ValidateOverrideJson(staleOverride, memberIds, presentSelectionSources, model!, groupInputVersion, requireCurrentAssociations: false)
                    || !ValidateEffectiveSelectionJson(effective, memberIds, memberCandidates)
                    || freshness == RecommendationFreshness.Stale
                        && (currentOverride.ValueKind != JsonValueKind.Null || staleOverride.ValueKind == JsonValueKind.Null || effective.ValueKind != JsonValueKind.Null)
                    || freshness == RecommendationFreshness.Current && staleOverride.ValueKind != JsonValueKind.Null)
                {
                    return RecommendationJsonReadOutcome.Failure("INVALID_OVERRIDE", "A recommendation override is invalid.");
                }
            }

            return RecommendationJsonReadOutcome.Success(schema, model, groups.GetArrayLength());
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or NullReferenceException)
        {
            return RecommendationJsonReadOutcome.Failure("MALFORMED_JSON", "The recommendation review artifact is malformed.");
        }
    }

    private static void ValidateDocument(RecommendationReviewExportDocument document)
    {
        foreach (RecommendationReviewExportGroup group in document.Groups)
        {
            ReviewedConsolidationRecommendation reviewed = group.Reviewed;
            ConsolidationRecommendation generated = reviewed.Generated;
            CalibreBook[] members = group.Members.OrderBy(value => value.Id.Value).ToArray();
            if (generated.ModelVersion != document.RecommendationModelVersion
                || !members.Select(value => value.Id).SequenceEqual(generated.MemberIds)
                || members.Select(value => value.Id).Distinct().Count() != members.Length)
            {
                throw new ArgumentException("Export members must exactly match the generated recommendation.", nameof(document));
            }

            Dictionary<(CalibreBookId BookId, string Format, string Path), BookFormat> memberFormats = members
                .SelectMany(book => book.Formats.Select(format => (book.Id, Format: format.Format.ToUpperInvariant(), Path: format.ExpectedRelativePath, Value: format)))
                .ToDictionary(value => (value.Id, value.Format, value.Path), value => value.Value);

            foreach (BookFormat format in members.SelectMany(value => value.Formats))
            {
                if (!IsSafeRelativePath(format.ExpectedRelativePath))
                {
                    throw new ArgumentException("Export format paths must be safe relative paths.", nameof(document));
                }
            }

            foreach (FormatSourceSelection selection in generated.FormatSelections)
            {
                foreach (RecommendationFormatCandidate candidate in selection.Candidates)
                {
                    if (!memberFormats.TryGetValue((candidate.BookId, candidate.Format, candidate.ExpectedRelativePath), out BookFormat? memberFormat)
                        || candidate.FileStatus != memberFormat.FileStatus
                        || candidate.Fingerprint != memberFormat.Fingerprint)
                    {
                        throw new ArgumentException("Generated format candidates must match current export members.", nameof(document));
                    }
                }
            }

            ValidateOverride(generated, reviewed.CurrentOverride, requireCurrentAssociations: true, nameof(document));
            ValidateOverride(generated, reviewed.StaleOverride, requireCurrentAssociations: false, nameof(document));

            if (reviewed.Freshness == RecommendationFreshness.Stale)
            {
                if (reviewed.CurrentOverride is not null
                    || reviewed.EffectiveSelection is not null
                    || reviewed.StaleOverride is null
                    || reviewed.StaleOverride.ModelVersion == generated.ModelVersion
                    && reviewed.StaleOverride.InputVersion == generated.InputVersion)
                {
                    throw new ArgumentException("A stale review must retain only a non-effective stale override.", nameof(document));
                }
            }
            else
            {
                if (reviewed.StaleOverride is not null)
                {
                    throw new ArgumentException("A current review cannot contain a stale override.", nameof(document));
                }

                ReviewedConsolidationRecommendation expected;
                if (reviewed.CurrentOverride is null)
                {
                    expected = ApplyRecommendationOverrideUseCase.Reset(generated);
                }
                else
                {
                    RecommendationOverrideOutcome outcome = ApplyRecommendationOverrideUseCase.Execute(generated, reviewed.CurrentOverride);
                    if (!outcome.IsSuccess)
                    {
                        throw new ArgumentException("A current review contains an invalid override.", nameof(document));
                    }

                    expected = outcome.Reviewed!;
                }

                if (reviewed.ReviewStatus != expected.ReviewStatus
                    || reviewed.Freshness != expected.Freshness
                    || !EffectiveSelectionsEqual(reviewed.EffectiveSelection, expected.EffectiveSelection))
                {
                    throw new ArgumentException("The effective reviewed selection does not match the validated override.", nameof(document));
                }
            }
        }
    }

    private static void ValidateOverride(
        ConsolidationRecommendation generated,
        UserRecommendationOverride? value,
        bool requireCurrentAssociations,
        string parameterName)
    {
        if (value is null) return;
        if (!Enum.IsDefined(value.RequestedStatus)
            || requireCurrentAssociations
            && (value.MetadataSourceBookId is not null && !generated.MemberIds.Contains(value.MetadataSourceBookId.Value)
                || value.RetainedSeparateBookIds.Any(id => !generated.MemberIds.Contains(id))))
        {
            throw new ArgumentException("The export contains an invalid review override.", parameterName);
        }

        foreach (FormatRecommendationOverride formatOverride in value.FormatOverrides)
        {
            FormatSourceSelection? generatedFormat = generated.FormatSelections.FirstOrDefault(selection => selection.Format == formatOverride.Format);
            if (string.IsNullOrWhiteSpace(formatOverride.Format)
                || !Enum.IsDefined(formatOverride.Action)
                || requireCurrentAssociations && generatedFormat is null
                || formatOverride.Action == FormatOverrideAction.SelectSource
                    && (formatOverride.SourceBookId is null
                        || requireCurrentAssociations && !generatedFormat!.Candidates.Any(candidate => candidate.BookId == formatOverride.SourceBookId
                            && candidate.FileStatus == FormatFileStatus.Present))
                || formatOverride.Action != FormatOverrideAction.SelectSource && formatOverride.SourceBookId is not null)
            {
                throw new ArgumentException("The export contains an invalid format override.", parameterName);
            }
        }
    }

    private static bool EffectiveSelectionsEqual(
        EffectiveRecommendationSelection? left,
        EffectiveRecommendationSelection? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.MetadataSourceBookId != right.MetadataSourceBookId
            || !left.RetainedSeparateBookIds.OrderBy(value => value.Value)
                .SequenceEqual(right.RetainedSeparateBookIds.OrderBy(value => value.Value))
            || left.FormatSelections.Count != right.FormatSelections.Count)
        {
            return false;
        }

        FormatSourceSelection[] leftFormats = left.FormatSelections.OrderBy(value => value.Format, StringComparer.Ordinal).ToArray();
        FormatSourceSelection[] rightFormats = right.FormatSelections.OrderBy(value => value.Format, StringComparer.Ordinal).ToArray();
        return leftFormats.Zip(rightFormats).All(pair => FormatSelectionsEqual(pair.First, pair.Second));
    }

    private static bool FormatSelectionsEqual(FormatSourceSelection left, FormatSourceSelection right) =>
        left.Format == right.Format
        && left.ResolutionStatus == right.ResolutionStatus
        && left.Strength == right.Strength
        && CandidateIdentity(left.ProposedSource) == CandidateIdentity(right.ProposedSource)
        && left.ReasonCodes.SequenceEqual(right.ReasonCodes)
        && left.WarningCodes.SequenceEqual(right.WarningCodes)
        && left.ProposedExcludedAlternatives.SequenceEqual(right.ProposedExcludedAlternatives)
        && left.Candidates.Count == right.Candidates.Count
        && left.Candidates.Zip(right.Candidates).All(pair => CandidatesEqual(pair.First, pair.Second));

    private static bool CandidatesEqual(RecommendationFormatCandidate left, RecommendationFormatCandidate right) =>
        CandidateIdentity(left) == CandidateIdentity(right)
        && left.FileStatus == right.FileStatus
        && left.Fingerprint == right.Fingerprint
        && left.Assessment?.Status == right.Assessment?.Status
        && left.Assessment?.Score == right.Assessment?.Score
        && left.Assessment?.AnalyzerVersion == right.Assessment?.AnalyzerVersion
        && left.Assessment?.ScoringModelVersion == right.Assessment?.ScoringModelVersion;

    private static (CalibreBookId BookId, string Format, string Path)? CandidateIdentity(RecommendationFormatCandidate? candidate) =>
        candidate is null ? null : (candidate.BookId, candidate.Format, candidate.ExpectedRelativePath);

    private static bool ValidateOverrideJson(
        JsonElement value,
        HashSet<long> memberIds,
        Dictionary<string, HashSet<long>> presentSelectionSources,
        string currentModelVersion,
        string currentInputVersion,
        bool requireCurrentAssociations)
    {
        if (value.ValueKind == JsonValueKind.Null) return true;
        string? modelVersion = RequiredString(value, "modelVersion");
        string? inputVersion = RequiredString(value, "inputVersion");
        if (value.ValueKind != JsonValueKind.Object
            || modelVersion is null
            || inputVersion is null
            || (requireCurrentAssociations
                ? modelVersion != currentModelVersion || inputVersion != currentInputVersion
                : modelVersion == currentModelVersion && inputVersion == currentInputVersion)
            || !ValidEnum<RecommendationReviewStatus>(RequiredString(value, "requestedStatus"))
            || !value.TryGetProperty("formatOverrides", out JsonElement overrides)
            || overrides.ValueKind != JsonValueKind.Array
            || !ValidateMemberIdArray(value, "retainedSeparateRecordIds", memberIds, out _, requireCurrentAssociations))
        {
            return false;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonElement formatOverride in overrides.EnumerateArray())
        {
            string? format = RequiredString(formatOverride, "format")?.ToUpperInvariant();
            string? actionText = RequiredString(formatOverride, "action");
            if (format is null || requireCurrentAssociations && !presentSelectionSources.ContainsKey(format) || !seen.Add(format)
                || !ValidEnum<FormatOverrideAction>(actionText))
            {
                return false;
            }

            FormatOverrideAction action = Enum.Parse<FormatOverrideAction>(actionText!);
            JsonElement source = formatOverride.GetProperty("sourceRecordId");
            if (action == FormatOverrideAction.SelectSource
                ? !source.TryGetInt64(out long sourceId)
                    || requireCurrentAssociations && (!memberIds.Contains(sourceId)
                        || !presentSelectionSources[format].Contains(sourceId))
                : source.ValueKind != JsonValueKind.Null)
            {
                return false;
            }
        }

        return !value.TryGetProperty("metadataSourceRecordId", out JsonElement metadataSource)
            || metadataSource.ValueKind == JsonValueKind.Null
            || metadataSource.TryGetInt64(out long id) && (!requireCurrentAssociations || memberIds.Contains(id));
    }

    private static bool ValidateEffectiveSelectionJson(
        JsonElement value,
        HashSet<long> memberIds,
        Dictionary<(long RecordId, string Format, string Path), FormatFileStatus> memberCandidates)
    {
        if (value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Object
            || !ValidateOptionalMemberId(value, "metadataSourceRecordId", memberIds)
            || !value.TryGetProperty("formatSelections", out JsonElement selections)
            || selections.ValueKind != JsonValueKind.Array
            || !ValidateMemberIdArray(value, "retainedSeparateRecordIds", memberIds, out HashSet<long> separate))
        {
            return false;
        }

        HashSet<string> formats = new(StringComparer.Ordinal);
        foreach (JsonElement selection in selections.EnumerateArray())
        {
            if (!ValidateSelectionJson(selection, memberCandidates, out string? format) || !formats.Add(format!)) return false;
            if (selection.TryGetProperty("sourceRecordId", out JsonElement source)
                && source.TryGetInt64(out long sourceId)
                && separate.Contains(sourceId)) return false;
        }

        return !value.GetProperty("metadataSourceRecordId").TryGetInt64(out long metadataId) || !separate.Contains(metadataId);
    }

    private static bool ValidateSelectionJson(
        JsonElement selection,
        Dictionary<(long RecordId, string Format, string Path), FormatFileStatus> memberCandidates,
        out string? format)
    {
        format = RequiredString(selection, "format")?.ToUpperInvariant();
        string? resolutionText = RequiredString(selection, "resolutionStatus");
        if (format is null
            || !ValidEnum<FormatResolutionStatus>(resolutionText)
            || !ValidEnum<RecommendationDecisionStrength>(RequiredString(selection, "decisionStrength"))
            || !selection.TryGetProperty("candidates", out JsonElement candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() is < 1 or > 10_000)
        {
            return false;
        }

        HashSet<long> presentSources = [];
        HashSet<(long RecordId, string Path)> seen = [];
        foreach (JsonElement candidate in candidates.EnumerateArray())
        {
            if (!candidate.TryGetProperty("recordId", out JsonElement recordIdElement)
                || !recordIdElement.TryGetInt64(out long recordId)
                || RequiredString(candidate, "relativePath") is not { } path
                || !seen.Add((recordId, path))
                || !memberCandidates.TryGetValue((recordId, format, path), out FormatFileStatus memberStatus)
                || RequiredString(candidate, "fileStatus") is not { } statusText
                || !Enum.TryParse(statusText, ignoreCase: false, out FormatFileStatus status)
                || status != memberStatus)
            {
                return false;
            }

            if (status == FormatFileStatus.Present) presentSources.Add(recordId);
        }

        FormatResolutionStatus resolution = Enum.Parse<FormatResolutionStatus>(resolutionText!);
        JsonElement source = selection.GetProperty("sourceRecordId");
        return resolution == FormatResolutionStatus.Selected
            ? source.TryGetInt64(out long sourceId) && presentSources.Contains(sourceId)
            : source.ValueKind == JsonValueKind.Null;
    }

    private static bool ValidateOptionalMemberId(JsonElement parent, string property, HashSet<long> memberIds)
    {
        if (!parent.TryGetProperty(property, out JsonElement value)) return false;
        return value.ValueKind == JsonValueKind.Null || value.TryGetInt64(out long id) && memberIds.Contains(id);
    }

    private static bool ValidateMemberIdArray(
        JsonElement parent,
        string property,
        HashSet<long> memberIds,
        out HashSet<long> values)
        => ValidateMemberIdArray(parent, property, memberIds, out values, requireCurrentAssociations: true);

    private static bool ValidateMemberIdArray(
        JsonElement parent,
        string property,
        HashSet<long> memberIds,
        out HashSet<long> values,
        bool requireCurrentAssociations)
    {
        values = [];
        if (!parent.TryGetProperty(property, out JsonElement array) || array.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (!element.TryGetInt64(out long id)
                || id <= 0
                || requireCurrentAssociations && !memberIds.Contains(id)
                || !values.Add(id)) return false;
        }

        return true;
    }

    private static bool IsSafeRelativePath(string path) => !string.IsNullOrWhiteSpace(path)
        && !Path.IsPathRooted(path)
        && !path.Contains(':', StringComparison.Ordinal)
        && !path.Split('/', '\\').Contains("..", StringComparer.Ordinal);

    private static bool ValidEnum<T>(string? value) where T : struct, Enum =>
        value is not null && Enum.TryParse(value, ignoreCase: false, out T parsed) && Enum.IsDefined(parsed);

    private static void WriteGroup(Utf8JsonWriter writer, RecommendationReviewExportGroup exportGroup)
    {
        ReviewedConsolidationRecommendation reviewed = exportGroup.Reviewed;
        ConsolidationRecommendation generated = reviewed.Generated;
        writer.WriteStartObject();
        writer.WriteString("duplicateGroupId", generated.GroupId.Value);
        writer.WriteString("normalizedMatchReason", "EXACT_NORMALIZED_TITLE_AUTHOR_SET");
        writer.WriteString("recommendationInputVersion", generated.InputVersion.Value);
        writer.WritePropertyName("members");
        writer.WriteStartArray();
        foreach (CalibreBook member in exportGroup.Members.OrderBy(value => value.Id.Value)) WriteMember(writer, member);
        writer.WriteEndArray();
        writer.WritePropertyName("generatedRecommendation");
        WriteGenerated(writer, generated);
        writer.WritePropertyName("reasons");
        writer.WriteStartArray();
        foreach (RecommendationReason reason in generated.Reasons) WriteReason(writer, reason);
        writer.WriteEndArray();
        writer.WritePropertyName("warnings");
        writer.WriteStartArray();
        foreach (RecommendationWarning warning in generated.Warnings) WriteWarning(writer, warning);
        writer.WriteEndArray();
        writer.WriteString("confidence", generated.Confidence.ToString());
        writer.WritePropertyName("userOverride");
        WriteOverride(writer, reviewed.CurrentOverride);
        writer.WritePropertyName("staleOverride");
        WriteOverride(writer, reviewed.StaleOverride);
        writer.WritePropertyName("effectiveFinalSelection");
        WriteEffectiveSelection(writer, reviewed.EffectiveSelection);
        writer.WriteString("reviewStatus", reviewed.ReviewStatus.ToString());
        writer.WriteString("freshness", reviewed.Freshness.ToString());
        UserRecommendationOverride? timestampSource = reviewed.CurrentOverride ?? reviewed.StaleOverride;
        if (timestampSource is null) writer.WriteNull("reviewedAtUtc");
        else writer.WriteString("reviewedAtUtc", timestampSource.ReviewedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteMember(Utf8JsonWriter writer, CalibreBook member)
    {
        writer.WriteStartObject();
        writer.WriteNumber("recordId", member.Id.Value);
        writer.WriteString("title", member.Title);
        writer.WriteString("authorSort", member.AuthorSort);
        writer.WritePropertyName("authors");
        writer.WriteStartArray(); foreach (BookAuthor author in member.Authors) writer.WriteStringValue(author.Name); writer.WriteEndArray();
        writer.WritePropertyName("identifiers");
        writer.WriteStartArray();
        foreach (BookIdentifier identifier in member.Identifiers.OrderBy(value => value.Type, StringComparer.Ordinal).ThenBy(value => value.Value, StringComparer.Ordinal))
        { writer.WriteStartObject(); writer.WriteString("type", identifier.Type); writer.WriteString("value", identifier.Value); writer.WriteEndObject(); }
        writer.WriteEndArray();
        WriteNullableString(writer, "publisher", member.PublicationMetadata.Publisher);
        if (member.PublicationMetadata.PublicationDate is null) writer.WriteNull("publicationDate"); else writer.WriteString("publicationDate", member.PublicationMetadata.PublicationDate.Value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        WriteNullableString(writer, "series", member.PublicationMetadata.Series);
        if (member.PublicationMetadata.SeriesIndex is null) writer.WriteNull("seriesIndex"); else writer.WriteNumber("seriesIndex", member.PublicationMetadata.SeriesIndex.Value);
        writer.WritePropertyName("languages"); writer.WriteStartArray(); foreach (string language in member.PublicationMetadata.Languages) writer.WriteStringValue(language); writer.WriteEndArray();
        writer.WriteBoolean("hasCover", member.PublicationMetadata.HasCover);
        writer.WritePropertyName("formats");
        writer.WriteStartArray();
        foreach (BookFormat format in member.Formats.OrderBy(value => value.Format, StringComparer.Ordinal).ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal))
        {
            writer.WriteStartObject(); writer.WriteString("format", format.Format); writer.WriteString("relativePath", format.ExpectedRelativePath); writer.WriteString("fileStatus", format.FileStatus.ToString());
            if (format.Fingerprint is null) { writer.WriteNull("sizeInBytes"); writer.WriteNull("sha256"); }
            else { writer.WriteNumber("sizeInBytes", format.Fingerprint.SizeInBytes); writer.WriteString("sha256", format.Fingerprint.Sha256.Value); }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteGenerated(Utf8JsonWriter writer, ConsolidationRecommendation generated)
    {
        writer.WriteStartObject();
        writer.WriteString("modelVersion", generated.ModelVersion.Value);
        if (generated.MetadataSource is null) writer.WriteNull("metadataSourceRecordId"); else writer.WriteNumber("metadataSourceRecordId", generated.MetadataSource.SelectedBookId.Value);
        writer.WritePropertyName("metadataDecision");
        WriteMetadataDecision(writer, generated.MetadataSource);
        writer.WritePropertyName("formatSelections"); writer.WriteStartArray(); foreach (FormatSourceSelection format in generated.FormatSelections) WriteFormatSelection(writer, format); writer.WriteEndArray();
        writer.WritePropertyName("proposedRedundantRecords"); writer.WriteStartArray(); foreach (RecordRecommendation record in generated.ProposedRedundantRecords) writer.WriteNumberValue(record.BookId.Value); writer.WriteEndArray();
        writer.WritePropertyName("retainedSeparateRecords"); writer.WriteStartArray(); foreach (RecordRecommendation record in generated.RetainedSeparateRecords) writer.WriteNumberValue(record.BookId.Value); writer.WriteEndArray();
        writer.WritePropertyName("recordDecisions"); writer.WriteStartArray(); foreach (RecordRecommendation record in generated.RecordRecommendations) WriteRecordDecision(writer, record); writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteMetadataDecision(Utf8JsonWriter writer, MetadataSourceSelection? selection)
    {
        if (selection is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("sourceRecordId", selection.SelectedBookId.Value);
        writer.WriteString("decisionStrength", selection.Strength.ToString());
        writer.WritePropertyName("reasonCodes"); writer.WriteStartArray(); foreach (string code in selection.ReasonCodes) writer.WriteStringValue(code); writer.WriteEndArray();
        writer.WritePropertyName("comparisons");
        writer.WriteStartArray();
        foreach (MetadataCandidateComparison comparison in selection.Comparisons)
        {
            writer.WriteStartObject();
            writer.WriteNumber("recordId", comparison.BookId.Value);
            writer.WriteBoolean("coreUsable", comparison.Vector.CoreUsable);
            writer.WriteBoolean("catalogIntegrity", comparison.Vector.CatalogIntegrity);
            writer.WriteNumber("conflictCount", comparison.Vector.ConflictCount);
            writer.WriteNumber("completenessCount", comparison.Vector.CompletenessCount);
            writer.WriteNumber("groupConsistencyCount", comparison.Vector.GroupConsistencyCount);
            writer.WriteNumber("validStrongIdentifierCount", comparison.Vector.ValidStrongIdentifierCount);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRecordDecision(Utf8JsonWriter writer, RecordRecommendation record)
    {
        writer.WriteStartObject();
        writer.WriteNumber("recordId", record.BookId.Value);
        writer.WriteString("kind", record.Kind.ToString());
        writer.WriteString("decisionStrength", record.Strength.ToString());
        writer.WritePropertyName("reasonCodes"); writer.WriteStartArray(); foreach (string code in record.ReasonCodes) writer.WriteStringValue(code); writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteFormatSelection(Utf8JsonWriter writer, FormatSourceSelection selection)
    {
        writer.WriteStartObject(); writer.WriteString("format", selection.Format); writer.WriteString("resolutionStatus", selection.ResolutionStatus.ToString());
        if (selection.ProposedSource is null) writer.WriteNull("sourceRecordId"); else writer.WriteNumber("sourceRecordId", selection.ProposedSource.BookId.Value);
        writer.WriteString("decisionStrength", selection.Strength.ToString());
        writer.WritePropertyName("reasonCodes"); writer.WriteStartArray(); foreach (string code in selection.ReasonCodes) writer.WriteStringValue(code); writer.WriteEndArray();
        writer.WritePropertyName("warningCodes"); writer.WriteStartArray(); foreach (string code in selection.WarningCodes) writer.WriteStringValue(code); writer.WriteEndArray();
        writer.WritePropertyName("candidates"); writer.WriteStartArray();
        foreach (RecommendationFormatCandidate candidate in selection.Candidates)
        {
            writer.WriteStartObject(); writer.WriteNumber("recordId", candidate.BookId.Value); writer.WriteString("relativePath", candidate.ExpectedRelativePath); writer.WriteString("fileStatus", candidate.FileStatus.ToString());
            writer.WriteBoolean("exactBinaryExcluded", selection.ProposedExcludedAlternatives.Any(value => value.BookId == candidate.BookId && value.ExpectedRelativePath == candidate.ExpectedRelativePath));
            if (candidate.Assessment is null) { writer.WriteNull("epubAssessmentStatus"); writer.WriteNull("epubQualityScore"); writer.WriteNull("analyzerVersion"); writer.WriteNull("scoringModelVersion"); }
            else { writer.WriteString("epubAssessmentStatus", candidate.Assessment.Status.ToString()); if (candidate.Assessment.Score is null) writer.WriteNull("epubQualityScore"); else writer.WriteNumber("epubQualityScore", candidate.Assessment.Score.Value.Value); writer.WriteString("analyzerVersion", candidate.Assessment.AnalyzerVersion.Value); writer.WriteString("scoringModelVersion", candidate.Assessment.ScoringModelVersion.Value); }
            writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
    }

    private static void WriteReason(Utf8JsonWriter writer, RecommendationReason reason)
    {
        writer.WriteStartObject(); writer.WriteString("code", reason.Code); writer.WriteString("subjectKind", reason.SubjectKind.ToString()); WriteNullableNumber(writer, "recordId", reason.BookId?.Value); WriteNullableString(writer, "format", reason.Format); writer.WriteString("explanation", reason.Explanation); WriteEvidence(writer, reason.Evidence); writer.WriteEndObject();
    }

    private static void WriteWarning(Utf8JsonWriter writer, RecommendationWarning warning)
    {
        writer.WriteStartObject(); writer.WriteString("code", warning.Code); writer.WriteString("severity", warning.Severity.ToString()); writer.WriteString("subjectKind", warning.SubjectKind.ToString()); WriteNullableNumber(writer, "recordId", warning.BookId?.Value); WriteNullableString(writer, "format", warning.Format); writer.WriteString("explanation", warning.Explanation); WriteEvidence(writer, warning.Evidence); writer.WriteEndObject();
    }

    private static void WriteEvidence(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> evidence)
    {
        writer.WritePropertyName("evidence"); writer.WriteStartArray(); foreach ((string key, string value) in evidence.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { writer.WriteStartObject(); writer.WriteString("key", key); writer.WriteString("value", value); writer.WriteEndObject(); }
        writer.WriteEndArray();
    }

    private static void WriteOverride(Utf8JsonWriter writer, UserRecommendationOverride? value)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject(); writer.WriteString("modelVersion", value.ModelVersion.Value); writer.WriteString("inputVersion", value.InputVersion.Value); writer.WriteString("requestedStatus", value.RequestedStatus.ToString()); if (value.MetadataSourceBookId is null) writer.WriteNull("metadataSourceRecordId"); else writer.WriteNumber("metadataSourceRecordId", value.MetadataSourceBookId.Value.Value);
        writer.WritePropertyName("formatOverrides"); writer.WriteStartArray(); foreach (FormatRecommendationOverride format in value.FormatOverrides) { writer.WriteStartObject(); writer.WriteString("format", format.Format); writer.WriteString("action", format.Action.ToString()); if (format.SourceBookId is null) writer.WriteNull("sourceRecordId"); else writer.WriteNumber("sourceRecordId", format.SourceBookId.Value.Value); writer.WriteEndObject(); }
        writer.WriteEndArray();
        writer.WritePropertyName("retainedSeparateRecordIds"); writer.WriteStartArray(); foreach (CalibreBookId id in value.RetainedSeparateBookIds) writer.WriteNumberValue(id.Value); writer.WriteEndArray(); writer.WriteEndObject();
    }

    private static void WriteEffectiveSelection(Utf8JsonWriter writer, EffectiveRecommendationSelection? selection)
    {
        if (selection is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject(); if (selection.MetadataSourceBookId is null) writer.WriteNull("metadataSourceRecordId"); else writer.WriteNumber("metadataSourceRecordId", selection.MetadataSourceBookId.Value.Value); writer.WritePropertyName("formatSelections"); writer.WriteStartArray(); foreach (FormatSourceSelection format in selection.FormatSelections.OrderBy(value => value.Format, StringComparer.Ordinal)) WriteFormatSelection(writer, format); writer.WriteEndArray(); writer.WritePropertyName("retainedSeparateRecordIds"); writer.WriteStartArray(); foreach (CalibreBookId id in selection.RetainedSeparateBookIds.OrderBy(value => value.Value)) writer.WriteNumberValue(id.Value); writer.WriteEndArray(); writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value) { if (value is null) writer.WriteNull(name); else writer.WriteString(name, value); }
    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, long? value) { if (value is null) writer.WriteNull(name); else writer.WriteNumber(name, value.Value); }
    private static string? RequiredString(JsonElement parent, string name) => parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
}

public sealed record RecommendationJsonReadOutcome(bool IsSuccess, string? SchemaVersion, string? ModelVersion, int GroupCount, RecommendationExportError? Error)
{
    public static RecommendationJsonReadOutcome Success(string schema, string model, int groupCount) => new(true, schema, model, groupCount, null);
    public static RecommendationJsonReadOutcome Failure(string code, string message) => new(false, null, null, 0, new(code, message));
}
