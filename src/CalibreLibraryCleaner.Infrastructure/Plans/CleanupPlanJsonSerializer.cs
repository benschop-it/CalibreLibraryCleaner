using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Infrastructure.Plans;

public static class CleanupPlanJsonSerializer
{
    public const int MaximumDepth = 64;
    public const int MaximumRecords = 100_000;
    public const int MaximumFormats = 100_000;
    public const int MaximumBackups = 100_000;
    public const int MaximumIssues = 10_000;
    public const int MaximumHistory = 10_000;
    public const int MaximumNestedItems = 100_000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = MaximumDepth,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    public static byte[] Serialize(CleanupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArtifactDto artifact = ToDto(plan);
        ValidateBounds(artifact);
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(artifact, Options);
        string normalized = Encoding.UTF8.GetString(raw).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
        return new UTF8Encoding(false).GetBytes(normalized);
    }

    public static CleanupPlanStoreReadResult Deserialize(byte[] bytes)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(bytes);
            string? schema = ValidateStructureAndReadSchema(bytes);
            if (schema is null)
                return CleanupPlanStoreReadResult.Failure("CLEANUP_PLAN_SCHEMA_REQUIRED", "A cleanup plan schema version is required.");
            if (!string.Equals(schema, CleanupPlanSchemaVersion.V1.Value, StringComparison.Ordinal))
                return CleanupPlanStoreReadResult.Failure("UNSUPPORTED_CLEANUP_PLAN_SCHEMA", "The cleanup plan uses an unsupported schema version.");
            ArtifactDto artifact = JsonSerializer.Deserialize<ArtifactDto>(bytes, Options)
                ?? throw new JsonException("The cleanup-plan document is empty.");
            ValidateBounds(artifact);
            bool unsupportedPolicy = !string.Equals(artifact.CleanupPlanPolicyVersion, CleanupPlanPolicyVersion.V1.Value, StringComparison.Ordinal);
            CleanupPlan plan = FromDto(artifact, unsupportedPolicy);
            _ = Serialize(plan); // Prove the reconstructed graph is canonically serializable.
            return CleanupPlanStoreReadResult.Success(plan);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException
            or FormatException or OverflowException or NullReferenceException)
        {
            return CleanupPlanStoreReadResult.Failure("CLEANUP_PLAN_JSON_INVALID", "The cleanup plan JSON is malformed, unsafe, inconsistent, or has an invalid canonical hash.");
        }
    }

    private static ArtifactDto ToDto(CleanupPlan plan)
    {
        CleanupPlanDefinition body = plan.Definition;
        return new()
        {
            SchemaVersion = plan.SchemaVersion.Value,
            CleanupPlanPolicyVersion = plan.PolicyVersion.Value,
            PlanId = plan.Id.ToString(),
            ArtifactRevision = plan.ArtifactRevision.Value,
            State = plan.State,
            ContentDigest = plan.ContentDigest.Value,
            InputIdentity = Input(plan.InputIdentity),
            SourceLibrary = new() { CalibreLibraryUuid = body.ExpectedLibraryState.LibraryUuid, SchemaVersion = body.ExpectedLibraryState.SchemaVersion },
            CreatedAtUtc = plan.CreatedAtUtc,
            LastValidatedAtUtc = plan.LastValidatedAtUtc,
            TargetRecordId = body.TargetRecordId.Value,
            InvolvedRecordIds = body.InvolvedRecordIds.Select(value => value.Value).ToArray(),
            MetadataRetention = new()
            {
                TargetRecordId = body.MetadataRetention.TargetRecordId.Value,
                SourceRecordId = body.MetadataRetention.SourceRecordId.Value,
                ExpectedMetadataStateRecordId = body.MetadataRetention.ExpectedMetadataStateRecordId.Value,
            },
            FormatRetentions = body.FormatRetentions.Select(value => new FormatRetentionDto
            {
                Id = value.Id,
                Format = value.Format,
                TargetRecordId = value.TargetRecordId.Value,
                SourceState = Format(value.SourceState),
                Mode = value.Mode,
                ReviewedSelectionReference = value.ReviewedSelectionReference,
                Preconditions = value.Preconditions.ToArray(),
            }).ToArray(),
            FormatRemovals = body.FormatRemovals.Select(value => new FormatRemovalDto
            {
                RecordId = value.RecordId.Value,
                Format = value.Format,
                RelativePath = value.RelativePath,
                ExpectedState = Format(value.ExpectedState),
                Reason = value.Reason,
                RetainedFormatInstructionId = value.RetainedFormatInstructionId,
                BackupRequirementId = value.BackupRequirementId,
                BytesIdenticalToRetainedSource = value.BytesIdenticalToRetainedSource,
            }).ToArray(),
            RecordRemovals = body.RecordRemovals.Select(value => new RecordRemovalDto
            {
                RecordId = value.RecordId.Value,
                BackupRequirementIds = value.BackupRequirementIds.ToArray(),
                RequiredRetainedFormatInstructionIds = value.RequiredRetainedFormatInstructionIds.ToArray(),
                Preconditions = value.Preconditions.ToArray(),
            }).ToArray(),
            ExpectedLibraryState = Expected(body.ExpectedLibraryState),
            BackupRequirements = body.BackupRequirements.Select(value => new BackupDto
            {
                Id = value.Id,
                Kind = value.Kind,
                RecordId = value.RecordId?.Value,
                Format = value.Format,
                Required = value.Required,
                Explanation = value.Explanation,
            }).ToArray(),
            Issues = plan.Validation.Issues.Select(value => new IssueDto
            {
                Code = value.Code,
                Severity = value.Severity,
                SubjectKind = value.SubjectKind,
                Explanation = value.Explanation,
                RecordId = value.RecordId?.Value,
                Format = value.Format,
                Evidence = value.Evidence.Select(pair => new EvidenceDto { Key = pair.Key, Value = pair.Value }).ToArray(),
            }).ToArray(),
            Provenance = Provenance(body.Provenance),
            Approval = plan.Approval is null ? null : new()
            {
                ApprovedAtUtc = plan.Approval.ApprovedAtUtc,
                Method = plan.Approval.Method,
                ApprovedRevision = plan.Approval.ApprovedRevision.Value,
                ContentDigest = plan.Approval.ContentDigest.Value,
            },
            Revocation = plan.Revocation is null ? null : new()
            {
                RevokedAtUtc = plan.Revocation.RevokedAtUtc,
                Reason = plan.Revocation.Reason,
                Method = plan.Revocation.Method,
                PriorApprovalRevision = plan.Revocation.PriorApprovalRevision.Value,
                ContentDigest = plan.Revocation.ContentDigest.Value,
            },
            LifecycleHistory = plan.LifecycleHistory.Select(value => new LifecycleDto
            {
                Revision = value.Revision.Value,
                FromState = value.FromState,
                ToState = value.ToState,
                ChangedAtUtc = value.ChangedAtUtc,
                Reason = value.Reason,
            }).ToArray(),
        };
    }

    private static CleanupPlan FromDto(ArtifactDto value, bool forceBlocked)
    {
        CleanupPlanContentDigest digest = new(value.ContentDigest);
        CleanupPlanInputIdentity input = new(
            value.InputIdentity.LibraryUuid,
            value.InputIdentity.SchemaVersion,
            new(value.InputIdentity.GroupId),
            value.InputIdentity.MemberIds.Select(id => new CalibreBookId(id)),
            new(value.InputIdentity.RecommendationModelVersion),
            new(value.InputIdentity.RecommendationInputVersion),
            new(value.InputIdentity.PolicyVersion),
            new(value.InputIdentity.DefinitionDigest));
        ExpectedLibraryState expected = FromExpected(value.ExpectedLibraryState);
        if (value.SourceLibrary.CalibreLibraryUuid != expected.LibraryUuid
            || value.SourceLibrary.SchemaVersion != expected.SchemaVersion
            || !value.InvolvedRecordIds.SequenceEqual(expected.MemberIds.Select(id => id.Value)))
            throw new ArgumentException("Source library and involved-record fields disagree.");
        CleanupPlanDefinition definition = new(
            expected,
            new(value.TargetRecordId),
            value.InvolvedRecordIds.Select(id => new CalibreBookId(id)),
            new(new(value.MetadataRetention.TargetRecordId), new(value.MetadataRetention.SourceRecordId), new(value.MetadataRetention.ExpectedMetadataStateRecordId)),
            value.FormatRetentions.Select(item =>
            {
                FormatRetentionInstruction result = new(item.Id, item.Format, new(item.TargetRecordId), FromFormat(item.SourceState), item.Mode, item.ReviewedSelectionReference);
                if (!result.Preconditions.SequenceEqual(item.Preconditions)) throw new ArgumentException("Retention preconditions are not canonical.");
                return result;
            }),
            value.FormatRemovals.Select(item => new FormatRemovalInstruction(
                new(item.RecordId), item.Format, item.RelativePath, FromFormat(item.ExpectedState), item.Reason,
                item.RetainedFormatInstructionId, item.BackupRequirementId, item.BytesIdenticalToRetainedSource)),
            value.RecordRemovals.Select(item =>
            {
                RecordRemovalInstruction result = new(new(item.RecordId), item.BackupRequirementIds, item.RequiredRetainedFormatInstructionIds);
                if (!result.Preconditions.SequenceEqual(item.Preconditions)) throw new ArgumentException("Record-removal preconditions are not canonical.");
                return result;
            }),
            value.BackupRequirements.Select(item => new BackupRequirement(item.Id, item.Kind,
                item.RecordId is null ? null : new CalibreBookId(item.RecordId.Value), item.Format, item.Required, item.Explanation)),
            FromProvenance(value.Provenance));
        if (value.CleanupPlanPolicyVersion != input.PolicyVersion.Value
            || value.CleanupPlanPolicyVersion != definition.Provenance.PolicyVersion.Value
            || value.SchemaVersion != definition.Provenance.SchemaVersion.Value
            || value.InputIdentity.GroupId != value.ExpectedLibraryState.GroupId
            || value.InputIdentity.GroupId != value.Provenance.GroupId
            || !value.InputIdentity.MemberIds.SequenceEqual(value.ExpectedLibraryState.MemberIds)
            || value.InputIdentity.LibraryUuid != value.ExpectedLibraryState.LibraryUuid
            || value.InputIdentity.SchemaVersion != value.ExpectedLibraryState.SchemaVersion)
            throw new ArgumentException("Cleanup-plan identity, policy, provenance, and expected-state fields disagree.");
        IReadOnlyList<CleanupPlanIssue> safetyIssues = CleanupPlanSafetyPolicy.Validate(definition);
        if (safetyIssues.Any(value => value.Severity == CleanupPlanIssueSeverity.BlockingError))
            throw new ArgumentException("The imported cleanup-plan safety graph is incomplete.");
        List<CleanupPlanIssue> importedIssues = value.Issues.Select(item => new CleanupPlanIssue(item.Code, item.Severity, item.SubjectKind, item.Explanation,
                item.RecordId is null ? null : new CalibreBookId(item.RecordId.Value), item.Format,
                item.Evidence.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))).ToList();
        if (!CleanupPlanRequiredIssuePolicy.ContainsAllRequired(definition, importedIssues))
            throw new ArgumentException("The imported cleanup plan omitted or altered required safety warnings or notices.");
        if (forceBlocked)
            importedIssues.Add(new("PLAN.POLICY_UNSUPPORTED", CleanupPlanIssueSeverity.BlockingError,
                CleanupPlanIssueSubjectKind.Plan, "The imported cleanup plan uses a future policy and is retained only as a blocked descriptive artifact."));
        CleanupPlanValidationResult validation = new(
            importedIssues,
            value.LastValidatedAtUtc,
            input);
        CleanupPlanApproval? approval = forceBlocked || value.Approval is null ? null : new(
            value.Approval.ApprovedAtUtc, value.Approval.Method, new(value.Approval.ApprovedRevision), new(value.Approval.ContentDigest));
        CleanupPlanRevocation? revocation = forceBlocked || value.Revocation is null ? null : new(
            value.Revocation.RevokedAtUtc, value.Revocation.Reason, value.Revocation.Method,
            new(value.Revocation.PriorApprovalRevision), new(value.Revocation.ContentDigest));
        CleanupPlanArtifactRevision artifactRevision = forceBlocked
            ? new(1)
            : new(value.ArtifactRevision);
        CleanupPlanState state = forceBlocked ? CleanupPlanState.Blocked : value.State;
        IEnumerable<CleanupPlanLifecycleEntry> history = forceBlocked
            ? [new(artifactRevision, CleanupPlanState.Draft, CleanupPlanState.Blocked, value.LastValidatedAtUtc, "Imported policy version is unsupported.")]
            : value.LifecycleHistory.Select(item => new CleanupPlanLifecycleEntry(
                new(item.Revision), item.FromState, item.ToState, item.ChangedAtUtc, item.Reason));
        return new(
            new(new Guid(value.PlanId)),
            new(value.SchemaVersion),
            new(value.CleanupPlanPolicyVersion),
            artifactRevision,
            state,
            digest,
            input,
            value.CreatedAtUtc,
            value.LastValidatedAtUtc,
            definition,
            validation,
            approval,
            revocation,
            history);
    }

    private static ExpectedLibraryDto Expected(ExpectedLibraryState value) => new()
    {
        LibraryUuid = value.LibraryUuid,
        SchemaVersion = value.SchemaVersion,
        GroupId = value.GroupId.Value,
        MemberIds = value.MemberIds.Select(id => id.Value).ToArray(),
        RecommendationModelVersion = value.RecommendationModelVersion.Value,
        RecommendationInputVersion = value.RecommendationInputVersion.Value,
        Records = value.Records.Select(record => new ExpectedRecordDto
        {
            RecordId = record.RecordId.Value,
            Title = record.Title,
            AuthorSort = record.AuthorSort,
            Authors = record.Authors.Select(author => new AuthorDto { Id = author.Id.Value, Name = author.Name, SortName = author.SortName }).ToArray(),
            Identifiers = record.Identifiers.Select(identifier => new IdentifierDto { Type = identifier.Type, Value = identifier.Value }).ToArray(),
            Publisher = record.Publisher,
            PublicationDate = record.PublicationDate,
            Series = record.Series,
            SeriesIndex = record.SeriesIndex,
            Languages = record.Languages.ToArray(),
            HasCover = record.HasCover,
            RelativeDirectory = record.RelativeDirectory,
            Formats = record.Formats.Select(Format).ToArray(),
        }).ToArray(),
    };

    private static ExpectedLibraryState FromExpected(ExpectedLibraryDto value) => new(
        value.LibraryUuid,
        value.SchemaVersion,
        new(value.GroupId),
        value.MemberIds.Select(id => new CalibreBookId(id)),
        value.Records.Select(record => new ExpectedRecordState(
            new(record.RecordId), record.Title, record.AuthorSort,
            record.Authors.Select(author => new ExpectedAuthorState(new(author.Id), author.Name, author.SortName)),
            record.Identifiers.Select(identifier => new ExpectedIdentifierState(identifier.Type, identifier.Value)),
            record.Publisher, record.PublicationDate, record.Series, record.SeriesIndex, record.Languages,
            record.HasCover, record.RelativeDirectory, record.Formats.Select(FromFormat))),
        new(value.RecommendationModelVersion),
        new(value.RecommendationInputVersion));

    private static ExpectedFormatDto Format(ExpectedFormatState value) => new()
    {
        RecordId = value.RecordId.Value,
        Format = value.Format,
        StoredFileName = value.StoredFileName,
        RelativePath = value.RelativePath,
        Status = value.Status,
        Length = value.Fingerprint.SizeInBytes,
        Sha256 = value.Fingerprint.Sha256.Value,
        CreationTimeUtc = value.Observation.CreationTimeUtc,
        LastWriteTimeUtc = value.Observation.LastWriteTimeUtc,
        Attributes = value.Observation.Attributes,
        ObservationSourceVersion = value.ObservationSourceVersion,
    };

    private static ExpectedFormatState FromFormat(ExpectedFormatDto value)
    {
        if (!string.Equals(value.ObservationSourceVersion, FormatFileObservation.SourceVersion, StringComparison.Ordinal))
            throw new ArgumentException("Unsupported format observation version.");
        return new(new(value.RecordId), value.Format, value.StoredFileName, value.RelativePath, value.Status,
            new(value.Length, new(value.Sha256)),
            new(value.Length, value.CreationTimeUtc, value.LastWriteTimeUtc, value.Attributes));
    }

    private static InputIdentityDto Input(CleanupPlanInputIdentity value) => new()
    {
        LibraryUuid = value.LibraryUuid,
        SchemaVersion = value.SchemaVersion,
        GroupId = value.GroupId.Value,
        MemberIds = value.MemberIds.Select(id => id.Value).ToArray(),
        RecommendationModelVersion = value.RecommendationModelVersion.Value,
        RecommendationInputVersion = value.RecommendationInputVersion.Value,
        PolicyVersion = value.PolicyVersion.Value,
        DefinitionDigest = value.DefinitionDigest.Value,
    };

    private static ProvenanceDto Provenance(CleanupPlanProvenance value) => new()
    {
        GroupId = value.GroupId.Value,
        NormalizedTitle = value.NormalizedTitle,
        NormalizedAuthors = value.NormalizedAuthors.ToArray(),
        GroupReasonCode = value.GroupReasonCode,
        RecommendationModelVersion = value.RecommendationModelVersion.Value,
        RecommendationInputVersion = value.RecommendationInputVersion.Value,
        GeneratedConfidence = value.GeneratedConfidence,
        GeneratedMetadataSourceRecordId = value.GeneratedMetadataSourceRecordId.Value,
        GeneratedFormatSelections = value.GeneratedFormatSelections.Select(Selection).ToArray(),
        GeneratedReasonCodes = value.GeneratedReasonCodes.ToArray(),
        GeneratedWarningCodes = value.GeneratedWarningCodes.ToArray(),
        ReviewStatus = value.ReviewStatus,
        Freshness = value.Freshness,
        ReviewedMetadataSourceRecordId = value.ReviewedMetadataSourceRecordId.Value,
        ReviewedFormatSelections = value.ReviewedFormatSelections.Select(Selection).ToArray(),
        UserOverride = new()
        {
            RequestedStatus = value.UserOverride.RequestedStatus,
            ReviewedAtUtc = value.UserOverride.ReviewedAtUtc,
            MetadataSourceRecordId = value.UserOverride.MetadataSourceRecordId?.Value,
            FormatActions = value.UserOverride.FormatActions.ToArray(),
            RetainedSeparateRecordIds = value.UserOverride.RetainedSeparateRecordIds.Select(id => id.Value).ToArray(),
        },
        SchemaVersion = value.SchemaVersion.Value,
        PolicyVersion = value.PolicyVersion.Value,
        CreatedAtUtc = value.CreatedAtUtc,
    };

    private static CleanupPlanProvenance FromProvenance(ProvenanceDto value) => new(
        new(value.GroupId), value.NormalizedTitle, value.NormalizedAuthors, value.GroupReasonCode,
        new(value.RecommendationModelVersion), new(value.RecommendationInputVersion), value.GeneratedConfidence,
        new(value.GeneratedMetadataSourceRecordId), value.GeneratedFormatSelections.Select(FromSelection),
        value.GeneratedReasonCodes, value.GeneratedWarningCodes, value.ReviewStatus, value.Freshness,
        new(value.ReviewedMetadataSourceRecordId), value.ReviewedFormatSelections.Select(FromSelection),
        new(value.UserOverride.RequestedStatus, value.UserOverride.ReviewedAtUtc,
            value.UserOverride.MetadataSourceRecordId is null ? null : new CalibreBookId(value.UserOverride.MetadataSourceRecordId.Value),
            value.UserOverride.FormatActions,
            value.UserOverride.RetainedSeparateRecordIds.Select(id => new CalibreBookId(id)).ToArray()),
        new(value.SchemaVersion), new(value.PolicyVersion), value.CreatedAtUtc);

    private static SelectionDto Selection(CleanupPlanFormatSelectionProvenance value) => new()
    {
        Format = value.Format,
        ResolutionStatus = value.ResolutionStatus,
        SelectedRecordId = value.SelectedRecordId?.Value,
        CandidateRecordIds = value.CandidateRecordIds.Select(id => id.Value).ToArray(),
        ReasonCodes = value.ReasonCodes.ToArray(),
        WarningCodes = value.WarningCodes.ToArray(),
    };

    private static CleanupPlanFormatSelectionProvenance FromSelection(SelectionDto value) => new(
        value.Format, value.ResolutionStatus,
        value.SelectedRecordId is null ? null : new CalibreBookId(value.SelectedRecordId.Value),
        value.CandidateRecordIds.Select(id => new CalibreBookId(id)).ToArray(), value.ReasonCodes, value.WarningCodes);

    private static void ValidateBounds(ArtifactDto value)
    {
        if (value.InvolvedRecordIds.Length > MaximumRecords || value.ExpectedLibraryState.Records.Length > MaximumRecords
            || value.ExpectedLibraryState.Records.Sum(record => (long)record.Formats.Length) > MaximumFormats
            || value.FormatRetentions.Length > MaximumFormats || value.FormatRemovals.Length > MaximumFormats
            || value.BackupRequirements.Length > MaximumBackups || value.Issues.Length > MaximumIssues
            || value.LifecycleHistory.Length > MaximumHistory
            || value.ExpectedLibraryState.Records.Sum(record => (long)record.Authors.Length + record.Identifiers.Length + record.Languages.Length) > MaximumNestedItems
            || value.RecordRemovals.Sum(item => (long)item.BackupRequirementIds.Length + item.RequiredRetainedFormatInstructionIds.Length + item.Preconditions.Length) > MaximumNestedItems
            || value.FormatRetentions.Sum(item => (long)item.Preconditions.Length) > MaximumNestedItems
            || value.Issues.Sum(item => (long)item.Evidence.Length) > MaximumNestedItems
            || SelectionItemCount(value.Provenance.GeneratedFormatSelections) > MaximumNestedItems
            || SelectionItemCount(value.Provenance.ReviewedFormatSelections) > MaximumNestedItems
            || (long)value.Provenance.GeneratedReasonCodes.Length + value.Provenance.GeneratedWarningCodes.Length
                + value.Provenance.NormalizedAuthors.Length + value.Provenance.UserOverride.FormatActions.Length
                + value.Provenance.UserOverride.RetainedSeparateRecordIds.Length > MaximumNestedItems)
            throw new JsonException("Cleanup plan collection bounds were exceeded.");
    }

    private static long SelectionItemCount(IEnumerable<SelectionDto> selections) => selections.Sum(value =>
        1L + value.CandidateRecordIds.Length + value.ReasonCodes.Length + value.WarningCodes.Length);

    private static string? ValidateStructureAndReadSchema(ReadOnlySpan<byte> bytes)
    {
        Utf8JsonReader reader = new(bytes, new JsonReaderOptions
        {
            MaxDepth = MaximumDepth,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        Stack<HashSet<string>?> containers = [];
        bool schemaValueExpected = false;
        string? schema = null;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    containers.Push(new(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    containers.Push(null);
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    if (containers.Count == 0) throw new JsonException("Invalid JSON container structure.");
                    containers.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (containers.Count == 0 || containers.Peek() is not HashSet<string> names)
                        throw new JsonException("A property appeared outside an object.");
                    string name = reader.GetString() ?? throw new JsonException("A JSON property name is invalid.");
                    if (!names.Add(name)) throw new JsonException("Duplicate JSON property.");
                    schemaValueExpected = containers.Count == 1 && name == "schemaVersion";
                    continue;
                default:
                    if (schemaValueExpected)
                    {
                        if (reader.TokenType != JsonTokenType.String) throw new JsonException("The cleanup-plan schema must be text.");
                        schema = reader.GetString();
                    }
                    break;
            }
            schemaValueExpected = false;
        }
        if (containers.Count != 0) throw new JsonException("The JSON document is incomplete.");
        return schema;
    }

    private sealed class ArtifactDto
    {
        public required string SchemaVersion { get; init; }
        public required string CleanupPlanPolicyVersion { get; init; }
        public required string PlanId { get; init; }
        public required int ArtifactRevision { get; init; }
        public required CleanupPlanState State { get; init; }
        public required string ContentDigest { get; init; }
        public required InputIdentityDto InputIdentity { get; init; }
        public required SourceLibraryDto SourceLibrary { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
        public required DateTimeOffset LastValidatedAtUtc { get; init; }
        public required long TargetRecordId { get; init; }
        public required long[] InvolvedRecordIds { get; init; }
        public required MetadataRetentionDto MetadataRetention { get; init; }
        public required FormatRetentionDto[] FormatRetentions { get; init; }
        public required FormatRemovalDto[] FormatRemovals { get; init; }
        public required RecordRemovalDto[] RecordRemovals { get; init; }
        public required ExpectedLibraryDto ExpectedLibraryState { get; init; }
        public required BackupDto[] BackupRequirements { get; init; }
        public required IssueDto[] Issues { get; init; }
        public required ProvenanceDto Provenance { get; init; }
        public required ApprovalDto? Approval { get; init; }
        public required RevocationDto? Revocation { get; init; }
        public required LifecycleDto[] LifecycleHistory { get; init; }
    }

    private sealed class InputIdentityDto
    {
        public required string LibraryUuid { get; init; }
        public required int SchemaVersion { get; init; }
        public required string GroupId { get; init; }
        public required long[] MemberIds { get; init; }
        public required string RecommendationModelVersion { get; init; }
        public required string RecommendationInputVersion { get; init; }
        public required string PolicyVersion { get; init; }
        public required string DefinitionDigest { get; init; }
    }
    private sealed class SourceLibraryDto { public required string CalibreLibraryUuid { get; init; } public required int SchemaVersion { get; init; } }
    private sealed class MetadataRetentionDto { public required long TargetRecordId { get; init; } public required long SourceRecordId { get; init; } public required long ExpectedMetadataStateRecordId { get; init; } }
    private sealed class FormatRetentionDto
    {
        public required string Id { get; init; }
        public required string Format { get; init; }
        public required long TargetRecordId { get; init; }
        public required ExpectedFormatDto SourceState { get; init; }
        public required FormatRetentionMode Mode { get; init; }
        public required string ReviewedSelectionReference { get; init; }
        public required string[] Preconditions { get; init; }
    }
    private sealed class FormatRemovalDto
    {
        public required long RecordId { get; init; }
        public required string Format { get; init; }
        public required string RelativePath { get; init; }
        public required ExpectedFormatDto ExpectedState { get; init; }
        public required FormatRemovalReason Reason { get; init; }
        public required string RetainedFormatInstructionId { get; init; }
        public required string BackupRequirementId { get; init; }
        public required bool BytesIdenticalToRetainedSource { get; init; }
    }
    private sealed class RecordRemovalDto
    {
        public required long RecordId { get; init; }
        public required string[] BackupRequirementIds { get; init; }
        public required string[] RequiredRetainedFormatInstructionIds { get; init; }
        public required string[] Preconditions { get; init; }
    }
    private sealed class ExpectedLibraryDto
    {
        public required string LibraryUuid { get; init; }
        public required int SchemaVersion { get; init; }
        public required string GroupId { get; init; }
        public required long[] MemberIds { get; init; }
        public required string RecommendationModelVersion { get; init; }
        public required string RecommendationInputVersion { get; init; }
        public required ExpectedRecordDto[] Records { get; init; }
    }
    private sealed class ExpectedRecordDto
    {
        public required long RecordId { get; init; }
        public required string Title { get; init; }
        public required string AuthorSort { get; init; }
        public required AuthorDto[] Authors { get; init; }
        public required IdentifierDto[] Identifiers { get; init; }
        public required string? Publisher { get; init; }
        public required DateTimeOffset? PublicationDate { get; init; }
        public required string? Series { get; init; }
        public required decimal? SeriesIndex { get; init; }
        public required string[] Languages { get; init; }
        public required bool HasCover { get; init; }
        public required string RelativeDirectory { get; init; }
        public required ExpectedFormatDto[] Formats { get; init; }
    }
    private sealed class AuthorDto { public required long Id { get; init; } public required string Name { get; init; } public required string SortName { get; init; } }
    private sealed class IdentifierDto { public required string Type { get; init; } public required string Value { get; init; } }
    private sealed class ExpectedFormatDto
    {
        public required long RecordId { get; init; }
        public required string Format { get; init; }
        public required string StoredFileName { get; init; }
        public required string RelativePath { get; init; }
        public required FormatFileStatus Status { get; init; }
        public required long Length { get; init; }
        public required string Sha256 { get; init; }
        public required DateTimeOffset CreationTimeUtc { get; init; }
        public required DateTimeOffset LastWriteTimeUtc { get; init; }
        public required int Attributes { get; init; }
        public required string ObservationSourceVersion { get; init; }
    }
    private sealed class BackupDto
    {
        public required string Id { get; init; }
        public required BackupRequirementKind Kind { get; init; }
        public required long? RecordId { get; init; }
        public required string? Format { get; init; }
        public required bool Required { get; init; }
        public required string Explanation { get; init; }
    }
    private sealed class IssueDto
    {
        public required string Code { get; init; }
        public required CleanupPlanIssueSeverity Severity { get; init; }
        public required CleanupPlanIssueSubjectKind SubjectKind { get; init; }
        public required string Explanation { get; init; }
        public required long? RecordId { get; init; }
        public required string? Format { get; init; }
        public required EvidenceDto[] Evidence { get; init; }
    }
    private sealed class EvidenceDto { public required string Key { get; init; } public required string Value { get; init; } }
    private sealed class ProvenanceDto
    {
        public required string GroupId { get; init; }
        public required string NormalizedTitle { get; init; }
        public required string[] NormalizedAuthors { get; init; }
        public required string GroupReasonCode { get; init; }
        public required string RecommendationModelVersion { get; init; }
        public required string RecommendationInputVersion { get; init; }
        public required RecommendationConfidence GeneratedConfidence { get; init; }
        public required long GeneratedMetadataSourceRecordId { get; init; }
        public required SelectionDto[] GeneratedFormatSelections { get; init; }
        public required string[] GeneratedReasonCodes { get; init; }
        public required string[] GeneratedWarningCodes { get; init; }
        public required RecommendationReviewStatus ReviewStatus { get; init; }
        public required RecommendationFreshness Freshness { get; init; }
        public required long ReviewedMetadataSourceRecordId { get; init; }
        public required SelectionDto[] ReviewedFormatSelections { get; init; }
        public required OverrideDto UserOverride { get; init; }
        public required string SchemaVersion { get; init; }
        public required string PolicyVersion { get; init; }
        public required DateTimeOffset CreatedAtUtc { get; init; }
    }
    private sealed class SelectionDto
    {
        public required string Format { get; init; }
        public required FormatResolutionStatus ResolutionStatus { get; init; }
        public required long? SelectedRecordId { get; init; }
        public required long[] CandidateRecordIds { get; init; }
        public required string[] ReasonCodes { get; init; }
        public required string[] WarningCodes { get; init; }
    }
    private sealed class OverrideDto
    {
        public required RecommendationReviewStatus RequestedStatus { get; init; }
        public required DateTimeOffset ReviewedAtUtc { get; init; }
        public required long? MetadataSourceRecordId { get; init; }
        public required string[] FormatActions { get; init; }
        public required long[] RetainedSeparateRecordIds { get; init; }
    }
    private sealed class ApprovalDto
    {
        public required DateTimeOffset ApprovedAtUtc { get; init; }
        public required CleanupPlanApprovalMethod Method { get; init; }
        public required int ApprovedRevision { get; init; }
        public required string ContentDigest { get; init; }
    }
    private sealed class RevocationDto
    {
        public required DateTimeOffset RevokedAtUtc { get; init; }
        public required string Reason { get; init; }
        public required CleanupPlanApprovalMethod Method { get; init; }
        public required int PriorApprovalRevision { get; init; }
        public required string ContentDigest { get; init; }
    }
    private sealed class LifecycleDto
    {
        public required int Revision { get; init; }
        public required CleanupPlanState FromState { get; init; }
        public required CleanupPlanState ToState { get; init; }
        public required DateTimeOffset ChangedAtUtc { get; init; }
        public required string Reason { get; init; }
    }
}
