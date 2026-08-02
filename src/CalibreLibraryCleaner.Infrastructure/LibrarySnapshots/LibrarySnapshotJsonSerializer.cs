using System.Globalization;
using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;

internal static class LibrarySnapshotJsonSerializer
{
    public const string SchemaVersion = "library-snapshot/1.0";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerSettings SerializerSettings = CreateSettings();

    public static byte[] Serialize(LibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string json = JsonConvert.SerializeObject(new SnapshotDocument(SchemaVersion, snapshot), SerializerSettings);
        return StrictUtf8.GetBytes(json + "\n");
    }

    public static LibrarySnapshotJsonReadResult Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            string json = StrictUtf8.GetString(utf8Json);
            using (JsonDocument parsed = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            }))
            {
                ValidateNoDuplicateProperties(parsed.RootElement);
            }

            SnapshotDocument? document = JsonConvert.DeserializeObject<SnapshotDocument>(json, SerializerSettings);
            if (document is null || !string.Equals(document.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
            {
                return LibrarySnapshotJsonReadResult.Failure("The persisted library snapshot version is not supported.");
            }

            return LibrarySnapshotJsonReadResult.Success(document.Snapshot);
        }
        catch (Exception exception) when (exception is Newtonsoft.Json.JsonException or System.Text.Json.JsonException
                                           or DecoderFallbackException or ArgumentException or InvalidOperationException
                                           or FormatException or OverflowException)
        {
            return LibrarySnapshotJsonReadResult.Failure(
                $"The persisted library snapshot is malformed or inconsistent: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static JsonSerializerSettings CreateSettings() => new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Culture = CultureInfo.InvariantCulture,
        DateFormatHandling = DateFormatHandling.IsoDateFormat,
        DateParseHandling = DateParseHandling.DateTimeOffset,
        DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind,
        FloatParseHandling = FloatParseHandling.Decimal,
        Formatting = Formatting.Indented,
        MaxDepth = 64,
        MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
        MissingMemberHandling = MissingMemberHandling.Error,
        TypeNameHandling = TypeNameHandling.None,
        Converters =
        {
            new ExactMetadataDuplicateGroupConverter(),
            new FormatAssessmentConverter(),
            new EpubAssessmentConverter(),
            new PdfAssessmentConverter(),
        },
    };

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (System.Text.Json.JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new System.Text.Json.JsonException("The persisted library snapshot contains a duplicate property.");
                }

                ValidateNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(item);
            }
        }
    }

    private sealed record SnapshotDocument(string SchemaVersion, LibrarySnapshot Snapshot);

    private sealed class ExactMetadataDuplicateGroupConverter : Newtonsoft.Json.JsonConverter<ExactMetadataDuplicateGroup>
    {
        public override ExactMetadataDuplicateGroup? ReadJson(
            JsonReader reader,
            Type objectType,
            ExactMetadataDuplicateGroup? existingValue,
            bool hasExistingValue,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            JObject document = JObject.Load(reader);
            string titleValue = document.Value<string>("normalizedTitle")
                ?? throw new JsonSerializationException("The normalized title is required.");
            string[] authorValues = document["normalizedAuthors"]?.ToObject<string[]>(serializer)
                ?? throw new JsonSerializationException("The normalized authors are required.");
            CalibreBookId[] members = document["members"]?.ToObject<CalibreBookId[]>(serializer)
                ?? throw new JsonSerializationException("The metadata-group members are required.");
            if (!MetadataTextNormalizer.TryNormalizeTitle(titleValue, out NormalizedTitle? title)
                || !MetadataTextNormalizer.TryCreateAuthorSet(authorValues, out NormalizedAuthorSet? authors)
                || !string.Equals(title!.Value, titleValue, StringComparison.Ordinal)
                || !authors!.Names.Select(value => value.Value).SequenceEqual(authorValues, StringComparer.Ordinal))
            {
                throw new JsonSerializationException("The normalized metadata identity is invalid.");
            }

            return new(new(title, authors), members);
        }

        public override void WriteJson(
            JsonWriter writer,
            ExactMetadataDuplicateGroup? value,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            ArgumentNullException.ThrowIfNull(value);
            JObject document = new()
            {
                ["normalizedTitle"] = value.Identity.Title.Value,
                ["normalizedAuthors"] = JArray.FromObject(
                    value.Identity.Authors.Names.Select(author => author.Value).ToArray(), serializer),
                ["members"] = JArray.FromObject(value.Members, serializer),
            };
            document.WriteTo(writer);
        }
    }

    private sealed class FormatAssessmentConverter : Newtonsoft.Json.JsonConverter<FormatAssessment>
    {
        public override FormatAssessment? ReadJson(
            JsonReader reader,
            Type objectType,
            FormatAssessment? existingValue,
            bool hasExistingValue,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            JObject document = JObject.Load(reader);
            CalibreBookId calibreBookId = document["calibreBookId"]?.ToObject<CalibreBookId>(serializer)
                ?? throw new JsonSerializationException("The assessment book ID is required.");
            string format = document.Value<string>("format")
                ?? throw new JsonSerializationException("The assessment format is required.");
            string expectedRelativePath = document.Value<string>("expectedRelativePath")
                ?? throw new JsonSerializationException("The assessment path is required.");
            FormatFileFingerprint? fingerprint = document["observedFingerprint"]?.ToObject<FormatFileFingerprint?>(serializer);
            FormatFileObservation? observation = document["observedObservation"]?.ToObject<FormatFileObservation?>(serializer);
            AssessmentStatus status = document["status"]?.ToObject<AssessmentStatus>(serializer)
                ?? throw new JsonSerializationException("The assessment status is required.");
            QualityScore? score = document["score"]?.ToObject<QualityScore?>(serializer);
            int? scoreCap = document.Value<int?>("scoreCap");
            AnalyzerVersion analyzerVersion = document["analyzerVersion"]?.ToObject<AnalyzerVersion>(serializer)
                ?? throw new JsonSerializationException("The analyzer version is required.");
            ScoringModelVersion scoringModelVersion = document["scoringModelVersion"]?.ToObject<ScoringModelVersion>(serializer)
                ?? throw new JsonSerializationException("The scoring model version is required.");
            AssessmentFinding[] findings = document["findings"]?.ToObject<AssessmentFinding[]>(serializer)
                ?? throw new JsonSerializationException("Assessment findings are required.");
            AssessmentScoreComponent[] components = document["scoreComponents"]?.ToObject<AssessmentScoreComponent[]>(serializer)
                ?? throw new JsonSerializationException("Assessment score components are required.");
            return new(
                calibreBookId,
                format,
                expectedRelativePath,
                fingerprint,
                status,
                score,
                analyzerVersion,
                scoringModelVersion,
                findings,
                components,
                observation,
                scoreCap);
        }

        public override void WriteJson(
            JsonWriter writer,
            FormatAssessment? value,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            ArgumentNullException.ThrowIfNull(value);
            JObject document = new()
            {
                ["calibreBookId"] = JToken.FromObject(value.CalibreBookId, serializer),
                ["format"] = value.Format,
                ["expectedRelativePath"] = value.ExpectedRelativePath,
                ["observedFingerprint"] = value.ObservedFingerprint is null
                    ? JValue.CreateNull()
                    : JToken.FromObject(value.ObservedFingerprint, serializer),
                ["observedObservation"] = value.ObservedObservation is null
                    ? JValue.CreateNull()
                    : JToken.FromObject(value.ObservedObservation, serializer),
                ["status"] = JToken.FromObject(value.Status, serializer),
                ["score"] = value.Score is null ? JValue.CreateNull() : JToken.FromObject(value.Score, serializer),
                ["scoreCap"] = value.ScoreCap is null ? JValue.CreateNull() : JToken.FromObject(value.ScoreCap, serializer),
                ["analyzerVersion"] = JToken.FromObject(value.AnalyzerVersion, serializer),
                ["scoringModelVersion"] = JToken.FromObject(value.ScoringModelVersion, serializer),
                ["findings"] = JArray.FromObject(value.Findings, serializer),
                ["scoreComponents"] = JArray.FromObject(
                    value.ScoreComponents.Select(component => new AssessmentScoreComponent(component.Id, component.MaximumScore)),
                    serializer),
            };
            document.WriteTo(writer);
        }
    }

    private sealed class EpubAssessmentConverter : Newtonsoft.Json.JsonConverter<EpubAssessment>
    {
        public override EpubAssessment? ReadJson(
            JsonReader reader,
            Type objectType,
            EpubAssessment? existingValue,
            bool hasExistingValue,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            JObject document = JObject.Load(reader);
            FormatAssessment result = document["result"]?.ToObject<FormatAssessment>(serializer)
                ?? throw new JsonSerializationException("The EPUB assessment result is required.");
            EpubFeatureSummary features = document["features"]?.ToObject<EpubFeatureSummary>(serializer)
                ?? throw new JsonSerializationException("The EPUB feature summary is required.");
            return new(result, features);
        }

        public override void WriteJson(
            JsonWriter writer,
            EpubAssessment? value,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            ArgumentNullException.ThrowIfNull(value);
            JObject document = new()
            {
                ["result"] = JToken.FromObject(value.Result, serializer),
                ["features"] = JToken.FromObject(value.Features, serializer),
            };
            document.WriteTo(writer);
        }
    }

    private sealed class PdfAssessmentConverter : Newtonsoft.Json.JsonConverter<PdfAssessment>
    {
        public override PdfAssessment? ReadJson(
            JsonReader reader,
            Type objectType,
            PdfAssessment? existingValue,
            bool hasExistingValue,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            JObject document = JObject.Load(reader);
            FormatAssessment result = document["result"]?.ToObject<FormatAssessment>(serializer)
                ?? throw new JsonSerializationException("The PDF assessment result is required.");
            PdfFeatureSummary features = document["features"]?.ToObject<PdfFeatureSummary>(serializer)
                ?? throw new JsonSerializationException("The PDF feature summary is required.");
            return new(result, features);
        }

        public override void WriteJson(
            JsonWriter writer,
            PdfAssessment? value,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            ArgumentNullException.ThrowIfNull(value);
            JObject document = new()
            {
                ["result"] = JToken.FromObject(value.Result, serializer),
                ["features"] = JToken.FromObject(value.Features, serializer),
            };
            document.WriteTo(writer);
        }
    }
}

internal sealed record LibrarySnapshotJsonReadResult(LibrarySnapshot? Snapshot, string? Error)
{
    public bool IsSuccess => Snapshot is not null;

    public static LibrarySnapshotJsonReadResult Success(LibrarySnapshot snapshot) => new(snapshot, null);

    public static LibrarySnapshotJsonReadResult Failure(string error) => new(null, error);
}
