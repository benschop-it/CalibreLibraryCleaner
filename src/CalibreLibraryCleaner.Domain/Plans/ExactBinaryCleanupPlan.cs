using System.Globalization;
using System.Text;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Domain.Plans;

public sealed record ExactBinaryCleanupPlanDefinition
{
    public ExactBinaryCleanupPlanDefinition(
        string libraryUuid,
        int librarySchemaVersion,
        ExactBinaryDuplicateGroupId groupId,
        FormatFileFingerprint fingerprint,
        ExpectedFormatState retainedFormat,
        IEnumerable<ExpectedFormatState> duplicateEvidence,
        IEnumerable<CalibreBookId> recordIdsToRemove,
        IEnumerable<ExpectedRecordState> expectedRecords,
        IEnumerable<BackupRequirement> backupRequirements,
        DateTimeOffset reviewedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryUuid);
        if (!Guid.TryParse(libraryUuid, out _)) throw new ArgumentException("The library UUID is invalid.", nameof(libraryUuid));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(librarySchemaVersion);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(retainedFormat);
        ArgumentNullException.ThrowIfNull(duplicateEvidence);
        ArgumentNullException.ThrowIfNull(recordIdsToRemove);
        ArgumentNullException.ThrowIfNull(expectedRecords);
        ArgumentNullException.ThrowIfNull(backupRequirements);

        ExpectedFormatState[] evidence = duplicateEvidence
            .OrderBy(value => value.RecordId.Value)
            .ThenBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.RelativePath, StringComparer.Ordinal)
            .ToArray();
        CalibreBookId[] removals = recordIdsToRemove.Distinct().OrderBy(value => value.Value).ToArray();
        ExpectedRecordState[] records = expectedRecords.OrderBy(value => value.RecordId.Value).ToArray();
        BackupRequirement[] backups = backupRequirements
            .OrderBy(value => value.Kind)
            .ThenBy(value => value.RecordId?.Value ?? 0)
            .ThenBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        if (removals.Length == 0) throw new ArgumentException("At least one duplicate record must be marked for deletion.", nameof(recordIdsToRemove));
        if (records.Length == 0 || records.Select(value => value.RecordId).Distinct().Count() != records.Length)
            throw new ArgumentException("Expected records must be non-empty and unique.", nameof(expectedRecords));
        if (backups.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() != backups.Length)
            throw new ArgumentException("Backup requirement IDs must be unique.", nameof(backupRequirements));

        ExpectedFormatState[] groupMembers = evidence.Prepend(retainedFormat).ToArray();
        if (groupMembers.Select(Association).Distinct().Count() != groupMembers.Length
            || groupMembers.Any(value => value.Fingerprint != fingerprint))
            throw new ArgumentException("Every exact-file evidence item must be distinct and match the group fingerprint.", nameof(duplicateEvidence));
        Dictionary<(CalibreBookId, string, string), ExpectedFormatState> expected = records
            .SelectMany(value => value.Formats)
            .ToDictionary(Association);
        if (groupMembers.Any(value => !expected.TryGetValue(Association(value), out ExpectedFormatState? current) || current != value)
            || removals.Contains(retainedFormat.RecordId)
            || !removals.All(recordId => evidence.Any(value => value.RecordId == recordId))
            || !removals.Append(retainedFormat.RecordId).Distinct().OrderBy(value => value.Value)
                .SequenceEqual(records.Select(value => value.RecordId)))
            throw new ArgumentException("Expected records must be exactly the keeper and explicitly marked duplicate records.", nameof(expectedRecords));

        LibraryUuid = libraryUuid.Trim();
        LibrarySchemaVersion = librarySchemaVersion;
        GroupId = groupId;
        Fingerprint = fingerprint;
        RetainedFormat = retainedFormat;
        DuplicateEvidence = Array.AsReadOnly(evidence);
        RecordIdsToRemove = Array.AsReadOnly(removals);
        ExpectedRecords = Array.AsReadOnly(records);
        BackupRequirements = Array.AsReadOnly(backups);
        ReviewedAtUtc = reviewedAtUtc.ToUniversalTime();
    }

    public string LibraryUuid { get; }
    public int LibrarySchemaVersion { get; }
    public ExactBinaryDuplicateGroupId GroupId { get; }
    public FormatFileFingerprint Fingerprint { get; }
    public ExpectedFormatState RetainedFormat { get; }
    public IReadOnlyList<ExpectedFormatState> DuplicateEvidence { get; }
    public IReadOnlyList<CalibreBookId> RecordIdsToRemove { get; }
    public IReadOnlyList<ExpectedRecordState> ExpectedRecords { get; }
    public IReadOnlyList<BackupRequirement> BackupRequirements { get; }
    public DateTimeOffset ReviewedAtUtc { get; }

    internal static (CalibreBookId RecordId, string Format, string Path) Association(ExpectedFormatState value) =>
        (value.RecordId, value.Format, value.RelativePath);
}

public sealed record ExactBinaryCleanupPlanInputIdentity
{
    public ExactBinaryCleanupPlanInputIdentity(
        string libraryUuid,
        int librarySchemaVersion,
        ExactBinaryDuplicateGroupId groupId,
        CleanupPlanPolicyVersion policyVersion,
        CleanupPlanContentDigest definitionDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryUuid);
        if (!Guid.TryParse(libraryUuid, out _)) throw new ArgumentException("The library UUID is invalid.", nameof(libraryUuid));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(librarySchemaVersion);
        LibraryUuid = libraryUuid.Trim();
        LibrarySchemaVersion = librarySchemaVersion;
        GroupId = groupId;
        PolicyVersion = policyVersion ?? throw new ArgumentNullException(nameof(policyVersion));
        DefinitionDigest = definitionDigest ?? throw new ArgumentNullException(nameof(definitionDigest));
    }

    public string LibraryUuid { get; }
    public int LibrarySchemaVersion { get; }
    public ExactBinaryDuplicateGroupId GroupId { get; }
    public CleanupPlanPolicyVersion PolicyVersion { get; }
    public CleanupPlanContentDigest DefinitionDigest { get; }
}

public sealed record ExactBinaryCleanupPlanValidationResult
{
    public ExactBinaryCleanupPlanValidationResult(
        IEnumerable<CleanupPlanIssue> issues,
        DateTimeOffset validatedAtUtc,
        ExactBinaryCleanupPlanInputIdentity? validatedInputIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = Array.AsReadOnly(issues
            .OrderBy(value => value.Severity)
            .ThenBy(value => value.RecordId?.Value ?? 0)
            .ThenBy(value => value.Format, StringComparer.Ordinal)
            .ThenBy(value => value.Code, StringComparer.Ordinal)
            .ThenBy(value => value.Explanation, StringComparer.Ordinal)
            .ToArray());
        ValidatedAtUtc = validatedAtUtc.ToUniversalTime();
        ValidatedInputIdentity = validatedInputIdentity;
    }

    public IReadOnlyList<CleanupPlanIssue> Issues { get; }
    public DateTimeOffset ValidatedAtUtc { get; }
    public ExactBinaryCleanupPlanInputIdentity? ValidatedInputIdentity { get; }
    public bool IsValid => Issues.All(value => value.Severity != CleanupPlanIssueSeverity.BlockingError);
    public IReadOnlyList<CleanupPlanIssue> BlockingErrors => Issues
        .Where(value => value.Severity == CleanupPlanIssueSeverity.BlockingError).ToArray();
}

public static class ExactBinaryCleanupPlanSafetyPolicy
{
    public static IReadOnlyList<CleanupPlanIssue> Validate(ExactBinaryCleanupPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<CleanupPlanIssue> issues = [];
        foreach (ExpectedRecordState record in definition.ExpectedRecords)
        {
            RequireBackup(definition, issues, BackupRequirementKind.RecordMetadataSnapshot, record.RecordId, null);
            if (record.HasCover) RequireBackup(definition, issues, BackupRequirementKind.CoverIfPresent, record.RecordId, null);
            foreach (ExpectedFormatState format in record.Formats)
            {
                RequireBackup(definition, issues, BackupRequirementKind.FormatFile, record.RecordId, format.Format);
                RequireBackup(definition, issues, BackupRequirementKind.ManagedPathAndFileState, record.RecordId, format.Format);
            }
        }
        RequireBackup(definition, issues, BackupRequirementKind.CleanupPlanArtifact, null, null);
        RequireBackup(definition, issues, BackupRequirementKind.ExecutionAudit, null, null);
        return issues;
    }

    private static void RequireBackup(
        ExactBinaryCleanupPlanDefinition definition,
        List<CleanupPlanIssue> issues,
        BackupRequirementKind kind,
        CalibreBookId? recordId,
        string? format)
    {
        bool present = definition.BackupRequirements.Any(value => value.Kind == kind
            && value.RecordId == recordId
            && string.Equals(value.Format, format, StringComparison.Ordinal));
        if (!present)
        {
            issues.Add(new("BINARY_PLAN.BACKUP_COVERAGE_INCOMPLETE", CleanupPlanIssueSeverity.BlockingError,
                CleanupPlanIssueSubjectKind.Backup, "The duplicate-record cleanup plan lacks complete backup coverage.", recordId, format));
        }
    }
}

public static class ExactBinaryCleanupPlanContentDigestPolicy
{
    private const string CanonicalVersion = "exact-binary-cleanup-plan-body-canonical/1.0";

    public static CleanupPlanContentDigest Compute(ExactBinaryCleanupPlanDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        StringBuilder value = new();
        Add(value, CanonicalVersion);
        Add(value, definition.LibraryUuid);
        Add(value, definition.LibrarySchemaVersion);
        Add(value, definition.GroupId.Value);
        Add(value, definition.Fingerprint.SizeInBytes);
        Add(value, definition.Fingerprint.Sha256.Value);
        AddFormat(value, definition.RetainedFormat);
        foreach (ExpectedFormatState evidence in definition.DuplicateEvidence) AddFormat(value, evidence);
        foreach (CalibreBookId recordId in definition.RecordIdsToRemove) Add(value, recordId.Value);
        foreach (ExpectedRecordState record in definition.ExpectedRecords) AddRecord(value, record);
        foreach (BackupRequirement backup in definition.BackupRequirements)
        {
            Add(value, backup.Id);
            Add(value, backup.Kind.ToString());
            Add(value, backup.RecordId?.Value);
            Add(value, backup.Format);
            Add(value, backup.Required);
            Add(value, backup.Explanation);
        }
        Add(value, definition.ReviewedAtUtc);
        return CleanupPlanContentDigest.FromCanonical(value.ToString());
    }

    private static void AddRecord(StringBuilder target, ExpectedRecordState record)
    {
        Add(target, record.RecordId.Value);
        Add(target, record.Title);
        Add(target, record.AuthorSort);
        foreach (ExpectedAuthorState author in record.Authors)
        {
            Add(target, author.Id.Value);
            Add(target, author.Name);
            Add(target, author.SortName);
        }
        foreach (ExpectedIdentifierState identifier in record.Identifiers)
        {
            Add(target, identifier.Type);
            Add(target, identifier.Value);
        }
        Add(target, record.Publisher);
        Add(target, record.PublicationDate);
        Add(target, record.Series);
        Add(target, record.SeriesIndex);
        foreach (string language in record.Languages) Add(target, language);
        Add(target, record.HasCover);
        Add(target, record.RelativeDirectory);
        foreach (ExpectedFormatState format in record.Formats) AddFormat(target, format);
    }

    private static void AddFormat(StringBuilder target, ExpectedFormatState format)
    {
        Add(target, format.RecordId.Value);
        Add(target, format.Format);
        Add(target, format.StoredFileName);
        Add(target, format.RelativePath);
        Add(target, format.Status.ToString());
        Add(target, format.Fingerprint.SizeInBytes);
        Add(target, format.Fingerprint.Sha256.Value);
        Add(target, format.Observation.Length);
        Add(target, format.Observation.CreationTimeUtc);
        Add(target, format.Observation.LastWriteTimeUtc);
        Add(target, format.Observation.Attributes);
        Add(target, format.ObservationSourceVersion);
    }

    private static void Add(StringBuilder target, object? item)
    {
        if (item is null)
        {
            target.Append("N;");
            return;
        }
        string text = item switch
        {
            DateTimeOffset date => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            bool flag => flag ? "1" : "0",
            IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
            _ => item.ToString() ?? string.Empty,
        };
        target.Append('V').Append(Encoding.UTF8.GetByteCount(text).ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(text).Append(';');
    }
}

public sealed record ExactBinaryCleanupPlan
{
    public ExactBinaryCleanupPlan(
        CleanupPlanId id,
        CleanupPlanSchemaVersion schemaVersion,
        CleanupPlanPolicyVersion policyVersion,
        CleanupPlanArtifactRevision artifactRevision,
        CleanupPlanState state,
        CleanupPlanContentDigest contentDigest,
        ExactBinaryCleanupPlanInputIdentity inputIdentity,
        DateTimeOffset createdAtUtc,
        ExactBinaryCleanupPlanDefinition definition,
        ExactBinaryCleanupPlanValidationResult validation,
        CleanupPlanApproval? approval,
        CleanupPlanRevocation? revocation,
        IEnumerable<CleanupPlanLifecycleEntry> lifecycleHistory)
    {
        CleanupPlanContentDigest computed = ExactBinaryCleanupPlanContentDigestPolicy.Compute(definition);
        if (computed != contentDigest || inputIdentity.DefinitionDigest != contentDigest
            || inputIdentity.LibraryUuid != definition.LibraryUuid
            || inputIdentity.LibrarySchemaVersion != definition.LibrarySchemaVersion
            || inputIdentity.GroupId != definition.GroupId
            || inputIdentity.PolicyVersion != policyVersion)
            throw new ArgumentException("Exact-binary cleanup plan identity does not match its immutable body.");
        CleanupPlanLifecycleEntry[] history = lifecycleHistory.OrderBy(value => value.Revision.Value).ToArray();
        ValidateLifecycle(state, artifactRevision, contentDigest, validation, approval, revocation, history);
        Id = id;
        SchemaVersion = schemaVersion;
        PolicyVersion = policyVersion;
        ArtifactRevision = artifactRevision;
        State = state;
        ContentDigest = contentDigest;
        InputIdentity = inputIdentity;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        Definition = definition;
        Validation = validation;
        Approval = approval;
        Revocation = revocation;
        LifecycleHistory = Array.AsReadOnly(history);
    }

    public CleanupPlanId Id { get; }
    public CleanupPlanSchemaVersion SchemaVersion { get; }
    public CleanupPlanPolicyVersion PolicyVersion { get; }
    public CleanupPlanArtifactRevision ArtifactRevision { get; }
    public CleanupPlanState State { get; }
    public CleanupPlanContentDigest ContentDigest { get; }
    public ExactBinaryCleanupPlanInputIdentity InputIdentity { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public ExactBinaryCleanupPlanDefinition Definition { get; }
    public ExactBinaryCleanupPlanValidationResult Validation { get; }
    public CleanupPlanApproval? Approval { get; }
    public CleanupPlanRevocation? Revocation { get; }
    public IReadOnlyList<CleanupPlanLifecycleEntry> LifecycleHistory { get; }

    private static void ValidateLifecycle(
        CleanupPlanState state,
        CleanupPlanArtifactRevision revision,
        CleanupPlanContentDigest digest,
        ExactBinaryCleanupPlanValidationResult validation,
        CleanupPlanApproval? approval,
        CleanupPlanRevocation? revocation,
        CleanupPlanLifecycleEntry[] history)
    {
        if (history.Length == 0 || history[^1].Revision != revision || history[^1].ToState != state
            || history[0].Revision.Value != 1 || history[0].FromState != CleanupPlanState.Draft
            || history.Where((value, index) => value.Revision.Value != index + 1).Any()
            || history.Where((value, index) => index > 0 && value.FromState != history[index - 1].ToState).Any()
            || history.Any(value => !CleanupPlan.IsLegal(value.FromState, value.ToState)))
            throw new ArgumentException("Exact-binary cleanup plan lifecycle history is invalid.", nameof(history));
        if (state is CleanupPlanState.Valid or CleanupPlanState.Approved && !validation.IsValid)
            throw new ArgumentException("Valid and approved plans cannot contain blocking issues.", nameof(validation));
        if (approval is not null && approval.ContentDigest != digest)
            throw new ArgumentException("Approval is not bound to the exact-binary cleanup plan.", nameof(approval));
        bool historyApproved = history.Any(value => value.ToState == CleanupPlanState.Approved);
        if (historyApproved != (approval is not null)
            || state == CleanupPlanState.Approved && revocation is not null
            || state == CleanupPlanState.Revoked && (approval is null || revocation is null)
            || state != CleanupPlanState.Revoked && revocation is not null)
            throw new ArgumentException("Exact-binary cleanup plan lifecycle audit is inconsistent.");
    }
}

public static class ExactBinaryCleanupPlanLifecyclePolicy
{
    public static ExactBinaryCleanupPlan Approve(
        ExactBinaryCleanupPlan plan,
        ExactBinaryCleanupPlanValidationResult validation,
        DateTimeOffset atUtc)
    {
        if (plan.State != CleanupPlanState.Valid || !validation.IsValid
            || validation.ValidatedInputIdentity != plan.InputIdentity)
            throw new InvalidOperationException("Only a current valid exact-binary cleanup plan can be approved.");
        CleanupPlanArtifactRevision revision = new(plan.ArtifactRevision.Value + 1);
        CleanupPlanApproval approval = new(atUtc.ToUniversalTime(), CleanupPlanApprovalMethod.ExplicitLocalUser,
            plan.ArtifactRevision, plan.ContentDigest);
        return Transition(plan, revision, CleanupPlanState.Approved, atUtc,
            "Explicit approval of marked duplicate-record removals.", validation, approval, null);
    }

    public static ExactBinaryCleanupPlan MarkStale(
        ExactBinaryCleanupPlan plan,
        ExactBinaryCleanupPlanValidationResult validation,
        DateTimeOffset atUtc)
    {
        if (plan.State is not (CleanupPlanState.Valid or CleanupPlanState.Approved)) return plan;
        return Transition(plan, new(plan.ArtifactRevision.Value + 1), CleanupPlanState.Stale, atUtc,
            "The exact-binary group or affected library state changed.", validation, plan.Approval, null);
    }

    private static ExactBinaryCleanupPlan Transition(
        ExactBinaryCleanupPlan plan,
        CleanupPlanArtifactRevision revision,
        CleanupPlanState state,
        DateTimeOffset atUtc,
        string reason,
        ExactBinaryCleanupPlanValidationResult validation,
        CleanupPlanApproval? approval,
        CleanupPlanRevocation? revocation)
    {
        if (!CleanupPlan.IsLegal(plan.State, state))
            throw new InvalidOperationException($"Illegal exact-binary cleanup plan transition {plan.State} -> {state}.");
        CleanupPlanLifecycleEntry[] history = plan.LifecycleHistory
            .Append(new(revision, plan.State, state, atUtc.ToUniversalTime(), reason)).ToArray();
        return new(plan.Id, plan.SchemaVersion, plan.PolicyVersion, revision, state, plan.ContentDigest,
            plan.InputIdentity, plan.CreatedAtUtc, plan.Definition, validation, approval, revocation, history);
    }
}
