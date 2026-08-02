using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;

namespace CalibreLibraryCleaner.Application.Plans;

public static class ExactBinaryCleanupPlanVersions
{
    public static CleanupPlanSchemaVersion Schema { get; } = new("exact-binary-cleanup-plan/2.0");
    public static CleanupPlanPolicyVersion Policy { get; } = new("exact-binary-cleanup-plan-policy/2.0.0");
}

public sealed record ExactBinaryCleanupPlanGenerationOutcome(
    ExactBinaryCleanupPlan? Plan,
    ExactBinaryCleanupPlanValidationResult Validation)
{
    public bool IsSuccess => Plan is not null && Validation.IsValid;
}

public sealed record ExactBinaryCleanupPlanOperationOutcome(
    ExactBinaryCleanupPlan? Plan,
    ExactBinaryCleanupPlanValidationResult Validation)
{
    public bool IsSuccess => Plan is not null && Validation.IsValid;
}

public sealed class GenerateExactBinaryCleanupPlanUseCase(
    ICleanupPlanIdGenerator idGenerator,
    IClock clock)
{
    public ExactBinaryCleanupPlanGenerationOutcome Execute(
        LibrarySnapshot snapshot,
        ExactBinaryDuplicateGroupId groupId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = clock.GetUtcNow().ToUniversalTime();
        List<CleanupPlanIssue> issues = [];
        ExactBinaryDuplicateGroup? group = snapshot.ExactBinaryDuplicateGroups
            .SingleOrDefault(value => value.Id == groupId);
        if (group is null)
        {
            issues.Add(Block("BINARY_PLAN.GROUP_NOT_CURRENT", "The exact-binary group is not present in the current scan."));
            return Failure(issues, now);
        }
        ExactBinaryRetentionDecision decision = ExactBinaryRetentionPolicy
            .Select(snapshot.ExactBinaryDuplicateGroups, snapshot.Books, cancellationToken)
            .Single(value => value.GroupId == groupId);
        if (!decision.IsEligible)
        {
            issues.Add(Block("BINARY_PLAN.FORMAT_LABEL_MISMATCH", decision.SkipReason
                ?? "The exact-binary group is not eligible for automatic retained-copy selection."));
            return Failure(issues, now);
        }
        ExactBinaryDuplicateMember retainedMember = decision.RetainedMember!;

        if (!group.SpansMultipleBookRecords)
            issues.Add(Block("BINARY_PLAN.MULTIPLE_RECORDS_REQUIRED",
                "This exact-file group does not contain removable copies on separate Calibre records."));
        if (issues.Count > 0) return Failure(issues, now);

        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books
            .Where(value => group.Members.Any(member => member.BookId == value.Id))
            .ToDictionary(value => value.Id);
        ExactBinaryDuplicateMember[] plannedMembers = group.Members.ToArray();
        foreach (ExactBinaryDuplicateMember member in plannedMembers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryFindCurrentFormat(books, member, group.Fingerprint, out _))
            {
                issues.Add(new("BINARY_PLAN.MEMBER_NOT_CURRENT", CleanupPlanIssueSeverity.BlockingError,
                    CleanupPlanIssueSubjectKind.Format,
                    "An exact-binary member no longer matches a present managed file with the group fingerprint.",
                    member.BookId, member.Format));
            }
        }
        foreach (CalibreBook book in books.Values)
        {
            foreach (BookFormat format in book.Formats)
            {
                if (format.FileStatus != FormatFileStatus.Present || format.Fingerprint is null || format.Observation is null)
                {
                    issues.Add(new("BINARY_PLAN.AFFECTED_RECORD_INCOMPLETE", CleanupPlanIssueSeverity.BlockingError,
                        CleanupPlanIssueSubjectKind.Format,
                        "Every format on an involved record must be present, hashed, and observed before duplicate-record cleanup can be planned.",
                        book.Id, format.Format));
                }
            }
        }
        if (issues.Count > 0) return Failure(issues, now);

        ExpectedRecordState[] records = books.Values.OrderBy(value => value.Id.Value)
            .Select(ExactBinaryExpectedState.CreateRecord).ToArray();
        Dictionary<(CalibreBookId, string, string), ExpectedFormatState> formats = records
            .SelectMany(value => value.Formats)
            .ToDictionary(value => (value.RecordId, value.Format, value.RelativePath));
        ExpectedFormatState retained = formats[Association(retainedMember)];
        ExpectedFormatState[] formatRemovals = decision.FormatRemovals
            .Select(value => formats[Association(value)]).ToArray();
        HashSet<(CalibreBookId, string, string)> removalAssociations = formatRemovals
            .Select(value => (value.RecordId, value.Format, value.RelativePath)).ToHashSet();
        CalibreBookId[] recordRemovals = records
            .Where(value => value.Formats.All(format => removalAssociations.Contains(
                (format.RecordId, format.Format, format.RelativePath))))
            .Select(value => value.RecordId)
            .ToArray();
        BackupRequirement[] backups = CreateBackups(records).ToArray();
        ExactBinaryCleanupPlanDefinition definition = new(
            snapshot.Identity.CalibreLibraryUuid,
            snapshot.Identity.SchemaVersion,
            group.Id,
            group.Fingerprint,
            retained,
            formatRemovals,
            recordRemovals,
            records,
            backups,
            now);
        issues.AddRange(ExactBinaryCleanupPlanSafetyPolicy.Validate(definition));
        issues.Add(new("BINARY_PLAN.AUTOMATIC_FORMAT_RETENTION", CleanupPlanIssueSeverity.Information,
            CleanupPlanIssueSubjectKind.Format,
            $"This plan automatically retains record {retained.RecordId.Value}'s {retained.Format}, removes {formatRemovals.Length} byte-identical format copy or copies, and removes only records that become completely empty. Other records and non-identical formats are preserved."));
        CleanupPlanContentDigest digest = ExactBinaryCleanupPlanContentDigestPolicy.Compute(definition);
        ExactBinaryCleanupPlanInputIdentity identity = new(
            definition.LibraryUuid,
            definition.LibrarySchemaVersion,
            definition.GroupId,
            ExactBinaryCleanupPlanVersions.Policy,
            digest);
        ExactBinaryCleanupPlanValidationResult validation = new(issues, now, identity);
        if (!validation.IsValid) return new(null, validation);

        CleanupPlanArtifactRevision revision = new(1);
        ExactBinaryCleanupPlan plan = new(
            idGenerator.Create(),
            ExactBinaryCleanupPlanVersions.Schema,
            ExactBinaryCleanupPlanVersions.Policy,
            revision,
            CleanupPlanState.Valid,
            digest,
            identity,
            now,
            definition,
            validation,
            null,
            null,
            [new(revision, CleanupPlanState.Draft, CleanupPlanState.Valid, now,
                "Current exact-binary membership and complete affected-record state were validated.")]);
        return new(plan, validation);
    }

    private static ExactBinaryCleanupPlanGenerationOutcome Failure(
        IEnumerable<CleanupPlanIssue> issues,
        DateTimeOffset now) => new(null, new(issues, now));

    private static CleanupPlanIssue Block(string code, string explanation) => new(
        code, CleanupPlanIssueSeverity.BlockingError, CleanupPlanIssueSubjectKind.Group, explanation);

    private static bool TryFindCurrentFormat(
        Dictionary<CalibreBookId, CalibreBook> books,
        ExactBinaryDuplicateMember member,
        FormatFileFingerprint fingerprint,
        out BookFormat? current)
    {
        current = null;
        if (!books.TryGetValue(member.BookId, out CalibreBook? book)) return false;
        current = book.Formats.SingleOrDefault(value => value.Format == member.Format
            && Normalize(value.ExpectedRelativePath) == Normalize(member.ExpectedRelativePath));
        return current is { FileStatus: FormatFileStatus.Present, Observation: not null }
            && current.Fingerprint == fingerprint;
    }

    private static IEnumerable<BackupRequirement> CreateBackups(IEnumerable<ExpectedRecordState> records)
    {
        foreach (ExpectedRecordState record in records)
        {
            yield return new($"metadata:{record.RecordId.Value}", BackupRequirementKind.RecordMetadataSnapshot,
                record.RecordId, null, true, "Back up the complete affected record metadata before removing a format.");
            if (record.HasCover)
            {
                yield return new($"cover:{record.RecordId.Value}", BackupRequirementKind.CoverIfPresent,
                    record.RecordId, null, true, "Back up and verify the affected record cover before removing a format.");
            }
            foreach (ExpectedFormatState format in record.Formats)
            {
                yield return new($"format:{format.RecordId.Value}:{format.Format}", BackupRequirementKind.FormatFile,
                    format.RecordId, format.Format, true, "Back up and verify this affected-record format file.");
                yield return new($"state:{format.RecordId.Value}:{format.Format}", BackupRequirementKind.ManagedPathAndFileState,
                    format.RecordId, format.Format, true, "Preserve this format's managed path and file-state observation.");
            }
        }
        yield return new("plan-artifact", BackupRequirementKind.CleanupPlanArtifact, null, null, true,
            "Preserve the approved immutable exact-binary cleanup plan.");
        yield return new("execution-audit", BackupRequirementKind.ExecutionAudit, null, null, true,
            "Preserve the complete exact-binary cleanup execution audit.");
    }

    private static (CalibreBookId, string, string) Association(ExactBinaryDuplicateMember value) =>
        (value.BookId, value.Format, Normalize(value.ExpectedRelativePath));

    private static string Normalize(string value) => value.Replace('\\', '/');
}

public sealed class ValidateExactBinaryCleanupPlanUseCase(IClock clock)
{
    public ExactBinaryCleanupPlanOperationOutcome Execute(
        ExactBinaryCleanupPlan plan,
        LibrarySnapshot currentSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.State is CleanupPlanState.Blocked or CleanupPlanState.Stale or CleanupPlanState.Revoked)
            return new(plan, plan.Validation);
        DateTimeOffset now = clock.GetUtcNow().ToUniversalTime();
        List<CleanupPlanIssue> issues = EvaluateStaleness(plan, currentSnapshot, cancellationToken).ToList();
        if (issues.Count > 0)
        {
            ExactBinaryCleanupPlanValidationResult staleValidation = new(
                plan.Validation.Issues.Where(value => value.Severity != CleanupPlanIssueSeverity.BlockingError)
                    .Concat(issues), now, plan.InputIdentity);
            return new(ExactBinaryCleanupPlanLifecyclePolicy.MarkStale(plan, staleValidation, now), staleValidation);
        }
        ExactBinaryCleanupPlanValidationResult current = new(plan.Validation.Issues, now, plan.InputIdentity);
        return new(plan, current);
    }

    internal static IReadOnlyList<CleanupPlanIssue> EvaluateStaleness(
        ExactBinaryCleanupPlan plan,
        LibrarySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ExactBinaryCleanupPlanDefinition definition = plan.Definition;
        if (snapshot.Identity.CalibreLibraryUuid != definition.LibraryUuid
            || snapshot.Identity.SchemaVersion != definition.LibrarySchemaVersion)
            return [Stale("The selected library identity or schema changed.")];
        ExactBinaryDuplicateGroup? group = snapshot.ExactBinaryDuplicateGroups
            .SingleOrDefault(value => value.Id == definition.GroupId);
        if (group is null || group.Fingerprint != definition.Fingerprint)
            return [Stale("The exact-binary group or shared fingerprint changed.")];
        HashSet<(CalibreBookId, string, string)> planned = definition.FormatRemovals
            .Prepend(definition.RetainedFormat)
            .Select(value => (value.RecordId, value.Format, value.RelativePath)).ToHashSet();
        HashSet<(CalibreBookId, string, string)> currentMembers = group.Members
            .Select(value => (value.BookId, value.Format, value.ExpectedRelativePath.Replace('\\', '/'))).ToHashSet();
        if (!planned.SetEquals(currentMembers)) return [Stale("The exact-binary group membership changed.")];

        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books.ToDictionary(value => value.Id);
        foreach (ExpectedRecordState expected in definition.ExpectedRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!books.TryGetValue(expected.RecordId, out CalibreBook? book)
                || !ExactBinaryExpectedState.Matches(expected, book))
                return [new("BINARY_PLAN.STALE", CleanupPlanIssueSeverity.BlockingError,
                    CleanupPlanIssueSubjectKind.Record,
                    "An affected record, its metadata, formats, paths, hashes, or observations changed.", expected.RecordId)];
        }
        return [];
    }

    private static CleanupPlanIssue Stale(string explanation) => new(
        "BINARY_PLAN.STALE", CleanupPlanIssueSeverity.BlockingError,
        CleanupPlanIssueSubjectKind.Plan, explanation);
}

public sealed class ApproveExactBinaryCleanupPlanUseCase(IClock clock)
{
    public ExactBinaryCleanupPlanOperationOutcome Execute(
        ExactBinaryCleanupPlan plan,
        LibrarySnapshot currentSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(currentSnapshot);
        DateTimeOffset now = clock.GetUtcNow().ToUniversalTime();
        IReadOnlyList<CleanupPlanIssue> stale = ValidateExactBinaryCleanupPlanUseCase
            .EvaluateStaleness(plan, currentSnapshot, cancellationToken);
        if (plan.State != CleanupPlanState.Valid || stale.Count > 0)
        {
            ExactBinaryCleanupPlanValidationResult failed = new(
                stale.Append(new("BINARY_PLAN.APPROVAL_NOT_ALLOWED", CleanupPlanIssueSeverity.BlockingError,
                    CleanupPlanIssueSubjectKind.Approval,
                    "Only a current valid exact-binary cleanup plan can be approved.")), now, plan.InputIdentity);
            return new(null, failed);
        }
        ExactBinaryCleanupPlanValidationResult validation = new(plan.Validation.Issues, now, plan.InputIdentity);
        ExactBinaryCleanupPlan approved = ExactBinaryCleanupPlanLifecyclePolicy.Approve(plan, validation, now);
        return new(approved, validation);
    }
}

internal static class ExactBinaryExpectedState
{
    public static ExpectedRecordState CreateRecord(CalibreBook book)
    {
        BookPublicationMetadata publication = book.PublicationMetadata;
        return new(
            book.Id,
            book.Title,
            book.AuthorSort,
            book.Authors.Select(value => new ExpectedAuthorState(
                value.Id ?? throw new InvalidOperationException("Projected authors require an explicit rescan before exact cleanup planning."),
                value.Name, value.SortName)),
            book.Identifiers.Select(value => new ExpectedIdentifierState(value.Type, value.Value)),
            publication.Publisher,
            publication.PublicationDate,
            publication.Series,
            publication.SeriesIndex,
            publication.Languages,
            publication.HasCover,
            book.RelativeDirectory,
            book.Formats.Select(format => new ExpectedFormatState(
                book.Id,
                format.Format,
                format.StoredFileName,
                format.ExpectedRelativePath,
                format.FileStatus,
                format.Fingerprint!,
                format.Observation!)));
    }

    public static bool Matches(ExpectedRecordState expected, CalibreBook current)
        => Matches(expected, current, new HashSet<(CalibreBookId, string, string)>());

    public static bool Matches(
        ExpectedRecordState expected,
        CalibreBook current,
        IReadOnlySet<(CalibreBookId RecordId, string Format, string RelativePath)> removedFormats)
    {
        if (expected.RecordId != current.Id
            || expected.Title != current.Title
            || expected.AuthorSort != current.AuthorSort
            || expected.RelativeDirectory != Normalize(current.RelativeDirectory)
            || expected.HasCover != current.PublicationMetadata.HasCover
            || expected.Publisher != current.PublicationMetadata.Publisher
            || expected.PublicationDate != current.PublicationMetadata.PublicationDate
            || expected.Series != current.PublicationMetadata.Series
            || expected.SeriesIndex != current.PublicationMetadata.SeriesIndex
            || current.Authors.Any(value => value.Id is null)
            || !expected.Authors.SequenceEqual(current.Authors.Select(value => new ExpectedAuthorState(
                value.Id!.Value, value.Name, value.SortName)))
            || !expected.Identifiers.SequenceEqual(current.Identifiers
                .Select(value => new ExpectedIdentifierState(value.Type, value.Value))
                .OrderBy(value => value.Type, StringComparer.Ordinal).ThenBy(value => value.Value, StringComparer.Ordinal))
            || !expected.Languages.SequenceEqual(current.PublicationMetadata.Languages))
            return false;
        if (current.Formats.Any(value => value.FileStatus != FormatFileStatus.Present
            || value.Fingerprint is null || value.Observation is null)) return false;
        ExpectedRecordState actual = CreateRecord(current);
        ExpectedFormatState[] remaining = expected.Formats
            .Where(value => !removedFormats.Contains((value.RecordId, value.Format, value.RelativePath)))
            .ToArray();
        return remaining.SequenceEqual(actual.Formats);
    }

    private static string Normalize(string value) => value.Replace('\\', '/');
}
