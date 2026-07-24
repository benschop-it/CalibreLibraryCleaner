using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;

namespace CalibreLibraryCleaner.Application.Plans;

public sealed class GenerateCleanupPlanUseCase(
    ICleanupPlanIdGenerator idGenerator,
    IClock clock)
{
    public CleanupPlanGenerationOutcome Execute(
        LibrarySnapshot snapshot,
        ReviewedConsolidationRecommendation reviewed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(reviewed);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = clock.GetUtcNow().ToUniversalTime();
        List<CleanupPlanIssue> eligibility = CleanupPlanEligibilityPolicy.Evaluate(snapshot, reviewed, cancellationToken).ToList();
        if (!CleanupPlanReviewConsistency.IsReconstructedFromCurrentOverride(reviewed))
            eligibility.Add(new("PLAN.REVIEW_INCONSISTENT", CleanupPlanIssueSeverity.BlockingError,
                CleanupPlanIssueSubjectKind.Provenance,
                "The reviewed selection is not the exact result of its attached current override."));
        if (eligibility.Any(value => value.Severity == CleanupPlanIssueSeverity.BlockingError))
            return new(null, new(eligibility, now));

        ConsolidationRecommendation generated = reviewed.Generated;
        EffectiveRecommendationSelection effective = reviewed.EffectiveSelection!;
        CalibreBookId target = effective.MetadataSourceBookId!.Value;
        ExactMetadataDuplicateGroup group = snapshot.ExactMetadataDuplicateGroups.Single(value => value.Id == generated.GroupId);
        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books
            .Where(value => generated.MemberIds.Contains(value.Id))
            .ToDictionary(value => value.Id);
        ExpectedRecordState[] records = books.Values.OrderBy(value => value.Id.Value)
            .Select(CreateExpectedRecord).ToArray();
        ExpectedLibraryState expectedLibrary = new(
            snapshot.Identity.CalibreLibraryUuid,
            snapshot.Identity.SchemaVersion,
            group.Id,
            group.Members,
            records,
            generated.ModelVersion,
            generated.InputVersion);
        Dictionary<(CalibreBookId RecordId, string Format, string Path), ExpectedFormatState> expectedFormats =
            records.SelectMany(value => value.Formats).ToDictionary(value => (value.RecordId, value.Format, value.RelativePath));

        List<BackupRequirement> backups = [];
        foreach (ExpectedRecordState record in records)
        {
            backups.Add(new($"metadata:{record.RecordId.Value}", BackupRequirementKind.RecordMetadataSnapshot, record.RecordId, null, true, "Back up the complete stored record metadata before any later cleanup."));
            if (record.HasCover)
                backups.Add(new($"cover:{record.RecordId.Value}", BackupRequirementKind.CoverIfPresent, record.RecordId, null, true, "Resolve, back up, and verify the Calibre-managed cover before any later cleanup."));
            foreach (ExpectedFormatState format in record.Formats)
            {
                backups.Add(new(FormatBackupId(format), BackupRequirementKind.FormatFile, record.RecordId, format.Format, true, "Back up and verify this exact affected format file."));
                backups.Add(new(StateBackupId(format), BackupRequirementKind.ManagedPathAndFileState, record.RecordId, format.Format, true, "Preserve this managed relative path and verified file-state observation."));
            }
        }
        backups.Add(new("plan-artifact", BackupRequirementKind.CleanupPlanArtifact, null, null, true, "Preserve the approved immutable cleanup-plan artifact."));
        backups.Add(new("execution-audit", BackupRequirementKind.ExecutionAudit, null, null, true, "A later execution milestone must preserve its complete audit result."));

        List<FormatRetentionInstruction> retentions = [];
        foreach (FormatSourceSelection selection in effective.FormatSelections.OrderBy(value => value.Format, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecommendationFormatCandidate source = selection.ProposedSource!;
            ExpectedFormatState state = expectedFormats[(source.BookId, selection.Format, Normalize(source.ExpectedRelativePath))];
            retentions.Add(new(
                $"retain:{selection.Format}",
                selection.Format,
                target,
                state,
                source.BookId == target ? FormatRetentionMode.RetainInTarget : FormatRetentionMode.RetainFromOtherRecord,
                $"review:{reviewed.ReviewStatus}:{selection.Format}:{source.BookId.Value}"));
        }

        Dictionary<string, FormatRetentionInstruction> retainedByFormat = retentions.ToDictionary(value => value.Format, StringComparer.Ordinal);
        List<FormatRemovalInstruction> formatRemovals = [];
        foreach (ExpectedFormatState existing in records.SelectMany(value => value.Formats))
        {
            FormatRetentionInstruction retained = retainedByFormat[existing.Format];
            if (existing == retained.SourceState)
            {
                if (existing.RecordId != target)
                    formatRemovals.Add(new(existing.RecordId, existing.Format, existing.RelativePath, existing,
                        FormatRemovalReason.RemovedWithSourceRecordAfterRetention, retained.Id, FormatBackupId(existing), false));
                continue;
            }
            bool identical = existing.Fingerprint == retained.SourceState.Fingerprint;
            FormatRemovalReason reason = identical
                ? FormatRemovalReason.ByteIdenticalAlternative
                : FormatRemovalReason.ReviewedNonIdenticalReplacement;
            formatRemovals.Add(new(existing.RecordId, existing.Format, existing.RelativePath, existing, reason,
                retained.Id, FormatBackupId(existing), identical));
        }
        RecordRemovalInstruction[] recordRemovals = records.Where(value => value.RecordId != target)
            .Select(record => new RecordRemovalInstruction(
                record.RecordId,
                backups.Where(value => value.RecordId == record.RecordId).Select(value => value.Id),
                retentions.Where(value => value.SourceState.RecordId == record.RecordId).Select(value => value.Id)))
            .ToArray();
        CleanupPlanProvenance provenance = CreateProvenance(group, generated, reviewed, now);
        CleanupPlanDefinition definition = new(
            expectedLibrary,
            target,
            group.Members,
            new(target, target, target),
            retentions,
            formatRemovals,
            recordRemovals,
            backups,
            provenance);
        List<CleanupPlanIssue> issues = CleanupPlanRequiredIssuePolicy.Create(definition).ToList();
        issues.AddRange(CleanupPlanSafetyPolicy.Validate(definition));
        CleanupPlanContentDigest digest = CleanupPlanContentDigestPolicy.Compute(definition);
        CleanupPlanInputIdentity inputIdentity = new(
            snapshot.Identity.CalibreLibraryUuid,
            snapshot.Identity.SchemaVersion,
            group.Id,
            group.Members,
            generated.ModelVersion,
            generated.InputVersion,
            CleanupPlanPolicyVersion.V1,
            digest);
        CleanupPlanValidationResult validation = new(issues, now, inputIdentity);
        if (!validation.IsValid) return new(null, validation);

        CleanupPlanId id = idGenerator.Create();
        CleanupPlanArtifactRevision revision = new(1);
        CleanupPlan plan = new(
            id,
            CleanupPlanSchemaVersion.V1,
            CleanupPlanPolicyVersion.V1,
            revision,
            CleanupPlanState.Valid,
            digest,
            inputIdentity,
            now,
            now,
            definition,
            validation,
            null,
            null,
            [new(revision, CleanupPlanState.Draft, CleanupPlanState.Valid, now, "Complete deterministic validation succeeded.")]);
        return new(plan, validation);
    }

    private static ExpectedRecordState CreateExpectedRecord(CalibreBook book)
    {
        BookPublicationMetadata publication = book.PublicationMetadata;
        return new(
            book.Id,
            book.Title,
            book.AuthorSort,
            book.Authors.Select(value => new ExpectedAuthorState(value.Id, value.Name, value.SortName)),
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

    private static CleanupPlanProvenance CreateProvenance(
        ExactMetadataDuplicateGroup group,
        ConsolidationRecommendation generated,
        ReviewedConsolidationRecommendation reviewed,
        DateTimeOffset createdAtUtc)
    {
        UserRecommendationOverride userOverride = reviewed.CurrentOverride!;
        return new(
            group.Id,
            group.Identity.Title.Value,
            group.Identity.Authors.Names.Select(value => value.Value),
            group.MatchReason.Code,
            generated.ModelVersion,
            generated.InputVersion,
            generated.Confidence,
            generated.MetadataSource!.SelectedBookId,
            MapSelections(generated.FormatSelections),
            generated.Reasons.Select(value => value.Code),
            generated.Warnings.Select(value => value.Code),
            reviewed.ReviewStatus,
            reviewed.Freshness,
            reviewed.EffectiveSelection!.MetadataSourceBookId!.Value,
            MapSelections(reviewed.EffectiveSelection.FormatSelections),
            new(
                userOverride.RequestedStatus,
                userOverride.ReviewedAtUtc,
                userOverride.MetadataSourceBookId,
                userOverride.FormatOverrides.Select(value => $"{value.Format}:{value.Action}:{value.SourceBookId?.Value}").Order(StringComparer.Ordinal).ToArray(),
                userOverride.RetainedSeparateBookIds),
            CleanupPlanSchemaVersion.V1,
            CleanupPlanPolicyVersion.V1,
            createdAtUtc);
    }

    private static IEnumerable<CleanupPlanFormatSelectionProvenance> MapSelections(IEnumerable<FormatSourceSelection> values) =>
        values.Select(value => new CleanupPlanFormatSelectionProvenance(
            value.Format,
            value.ResolutionStatus,
            value.ProposedSource?.BookId,
            value.Candidates.Select(candidate => candidate.BookId).ToArray(),
            value.ReasonCodes,
            value.WarningCodes));

    private static string FormatBackupId(ExpectedFormatState value) => $"format:{value.RecordId.Value}:{value.Format}";
    private static string StateBackupId(ExpectedFormatState value) => $"state:{value.RecordId.Value}:{value.Format}";
    private static string Normalize(string path) => path.Replace('\\', '/');
}
