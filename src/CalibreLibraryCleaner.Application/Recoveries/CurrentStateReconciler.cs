using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;

namespace CalibreLibraryCleaner.Application.Recoveries;

public sealed class CurrentStateReconciler : ICurrentStateReconciler
{
    public CurrentStateReconciliation Reconcile(
        RecoverySourceInspection source,
        RecoveryCurrentStateSnapshot currentState)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(currentState);
        if (!source.IsVerified || source.CleanupPlan is null || source.Journal is null)
            return CurrentStateReconciliation.Create(
                source.Journal?.ExecutionId.ToString() ?? "unverified",
                currentState.Snapshot.Identity.CalibreLibraryUuid,
                currentState.Snapshot.Identity.SchemaVersion,
                currentState.FullFingerprint, currentState.AffectedFingerprint,
                currentState.UnrelatedFingerprint, source.Journal?.Operations ?? [], [],
                [Block("RECOVERY.SOURCE_NOT_VERIFIED", "Source artifacts", "The source execution is not a verified reconciliation input.")]);

        CleanupPlan plan = source.CleanupPlan;
        VerifiedExecutionJournalProjection journal = source.Journal;
        Dictionary<CalibreBookId, CalibreBook> currentById = currentState.Snapshot.Books.ToDictionary(value => value.Id);
        List<RecoveryIssue> issues = [.. source.Issues];
        if (!string.Equals(plan.InputIdentity.LibraryUuid, currentState.Snapshot.Identity.CalibreLibraryUuid, StringComparison.Ordinal)
            || plan.InputIdentity.SchemaVersion != currentState.Snapshot.Identity.SchemaVersion)
            issues.Add(Block("RECOVERY.LIBRARY_IDENTITY_MISMATCH", "Current library",
                "The selected current library does not match the affected cleanup library."));

        Dictionary<ExecutionOperationId, SourceOperationProjection> journalOperations =
            journal.Operations.ToDictionary(value => value.OperationId);
        Dictionary<string, FormatRetentionInstruction> retentions =
            plan.Definition.FormatRetentions.ToDictionary(value => value.Format, StringComparer.Ordinal);
        HashSet<CalibreBookId> durablyRemovedRecords = journal.Operations
            .Where(value => value.Kind == ExecutionOperationKind.RemoveRedundantRecord
                && value.State == DurableSourceOperationState.DurablyCompleted
                && value.SourceRecordId is not null)
            .Select(value => value.SourceRecordId!.Value).ToHashSet();
        List<ReconciledRecoveryRecord> reconciledRecords = [];
        HashSet<CalibreBookId> claimedCurrentIds = plan.Definition.ExpectedLibraryState.Records
            .Select(expected => (Expected: expected, Current: currentById.GetValueOrDefault(expected.RecordId)))
            .Where(pair => pair.Current is not null
                && (IsExactPreState(pair.Expected, pair.Current)
                    || IsExpectedPostState(plan, pair.Expected, pair.Current, journalOperations)
                    || IsIndependentlyModifiedRetainedTarget(
                        plan, pair.Expected, pair.Current, journalOperations)))
            .Select(pair => pair.Current!.Id)
            .ToHashSet();

        foreach (ExpectedRecordState expected in plan.Definition.ExpectedLibraryState.Records)
        {
            LogicalRecoveryRecordId logicalId = LogicalRecoveryRecordId.Create(journal.ExecutionId, expected.RecordId);
            CalibreBook? sameId = currentById.GetValueOrDefault(expected.RecordId);
            List<CalibreBook> semanticCandidates = FindSemanticCandidates(expected, currentState.Snapshot.Books)
                .Where(value => (sameId is null || value.Id != sameId.Id)
                    && !claimedCurrentIds.Contains(value.Id)).ToList();
            CalibreBook? matched = null;
            List<RecoveryReconciliationClassification> classifications = [];
            List<CalibreBookId> ambiguous = [];

            bool sameIdIsOriginal = sameId is not null && IsExactPreState(expected, sameId);
            bool sameIdIsExpectedPost = sameId is not null && IsExpectedPostState(plan, expected, sameId, journalOperations);
            if (sameIdIsOriginal)
            {
                matched = sameId;
                classifications.Add(durablyRemovedRecords.Contains(expected.RecordId)
                    ? RecoveryReconciliationClassification.AlreadyRestored
                    : RecoveryReconciliationClassification.UnchangedFromPreState);
            }
            else if (sameIdIsExpectedPost)
            {
                matched = sameId;
                classifications.Add(RecoveryReconciliationClassification.MatchesExpectedPostState);
                classifications.Add(RecoveryReconciliationClassification.CompletedAndMatchesJournalPostState);
            }
            else if (sameId is not null && IsIndependentlyModifiedRetainedTarget(
                         plan, expected, sameId, journalOperations))
            {
                matched = sameId;
                classifications.Add(RecoveryReconciliationClassification.IndependentlyModifiedAfterExecution);
            }
            else if (sameId is not null)
            {
                classifications.Add(RecoveryReconciliationClassification.Ambiguous);
                classifications.Add(RecoveryReconciliationClassification.UnexpectedUniqueContent);
                ambiguous.Add(sameId.Id);
                issues.Add(Block("RECOVERY.NUMERIC_ID_REUSED", $"Original record {expected.RecordId.Value}",
                    "The original numeric ID is now occupied by semantically different content.", logicalId, sameId.Id));
            }
            else if (semanticCandidates.Count == 1)
            {
                matched = semanticCandidates[0];
                claimedCurrentIds.Add(matched.Id);
                classifications.Add(RecoveryReconciliationClassification.IndependentlyRecreated);
                classifications.Add(RecoveryReconciliationClassification.DifferentCurrentRecordId);
                issues.Add(new("RECOVERY.RECORD_ID_CHANGED", RecoveryIssueSeverity.AcknowledgementRequired,
                    $"Logical record {logicalId}", "The semantic record uses a different Calibre numeric ID.",
                    logicalId, matched.Id));
            }
            else if (semanticCandidates.Count > 1)
            {
                classifications.Add(RecoveryReconciliationClassification.Ambiguous);
                ambiguous.AddRange(semanticCandidates.Select(value => value.Id));
                issues.Add(Block("RECOVERY.RECORD_MATCH_AMBIGUOUS", $"Logical record {logicalId}",
                    "More than one current record is a plausible semantic match.", logicalId));
            }
            else
            {
                classifications.Add(RecoveryReconciliationClassification.Missing);
            }

            SourceOperationProjection? removal = journalOperations.Values.SingleOrDefault(value =>
                value.Kind == ExecutionOperationKind.RemoveRedundantRecord
                && value.SourceRecordId == expected.RecordId);
            if (removal is not null)
                AddRemovalJournalClassification(removal, expected, matched, classifications);

            List<ReconciledFormat> formats = ReconcileFormats(
                source, plan, expected, matched, logicalId, journalOperations, issues);
            RecoveryCoverEvidence? cover = matched is null ? null
                : currentState.Covers.SingleOrDefault(value =>
                    value.RecordId == matched.Id);
            if (expected.HasCover || matched?.PublicationMetadata.HasCover == true)
            {
                issues.Add(Block("RECOVERY.COVER_STATE_UNSUPPORTED",
                    $"Logical record {logicalId}",
                    "This source or current record has cover content, but the exact cover restore/preservation mapping is not qualified.",
                    logicalId, matched?.Id));
                if (expected.HasCover != (cover?.HasCover == true)
                    && !classifications.Contains(
                        RecoveryReconciliationClassification.UnexpectedUniqueContent))
                    classifications.Add(
                        RecoveryReconciliationClassification.UnexpectedUniqueContent);
            }
            if (formats.Any(value =>
                    value.Classification == RecoveryReconciliationClassification.UnexpectedUniqueContent)
                && !classifications.Contains(RecoveryReconciliationClassification.UnexpectedUniqueContent))
                classifications.Add(RecoveryReconciliationClassification.UnexpectedUniqueContent);
            if (formats.Any(value =>
                    value.Classification == RecoveryReconciliationClassification.IndependentlyModifiedAfterExecution)
                && !classifications.Contains(RecoveryReconciliationClassification.IndependentlyModifiedAfterExecution))
                classifications.Add(RecoveryReconciliationClassification.IndependentlyModifiedAfterExecution);
            RecoveryRecordIdentity identity = new(logicalId, expected.RecordId, matched?.Id, null,
                RecoveryRecordFingerprintPolicy.Compute(expected),
                matched is null ? null : RecoveryRecordFingerprintPolicy.Compute(matched),
                expected.Identifiers.Select(value => $"{value.Type}:{value.Value}"),
                expected.Formats.Select(format => new RecoveryFormatIdentity(format.Format,
                    format.Fingerprint, source.FindOriginalFormat(expected.RecordId, format.Format)?.RelativePath
                        ?? $"missing:{expected.RecordId.Value}:{format.Format}")),
                $"record:{expected.RecordId.Value}");
            reconciledRecords.Add(new(identity, classifications, formats, ambiguous));
        }

        HashSet<CalibreBookId> affectedIds = plan.Definition.ExpectedLibraryState.Records
            .Select(value => value.RecordId).ToHashSet();
        affectedIds.UnionWith(reconciledRecords
            .Where(value => value.Identity.CurrentRecordId is not null)
            .Select(value => value.Identity.CurrentRecordId!.Value));
        return CurrentStateReconciliation.Create(journal.ExecutionId.ToString(),
            currentState.Snapshot.Identity.CalibreLibraryUuid, currentState.Snapshot.Identity.SchemaVersion,
            RecoverySnapshotFingerprintPolicy.ComputeFull(currentState.Snapshot, currentState.Covers),
            RecoverySnapshotFingerprintPolicy.ComputeAffected(
                currentState.Snapshot, affectedIds, currentState.Covers),
            RecoverySnapshotFingerprintPolicy.ComputeUnrelated(
                currentState.Snapshot, affectedIds, currentState.Covers),
            journal.Operations, reconciledRecords, issues);
    }

    private static List<ReconciledFormat> ReconcileFormats(
        RecoverySourceInspection source,
        CleanupPlan plan,
        ExpectedRecordState expected,
        CalibreBook? current,
        LogicalRecoveryRecordId logicalId,
        IReadOnlyDictionary<ExecutionOperationId, SourceOperationProjection> journalOperations,
        List<RecoveryIssue> issues)
    {
        Dictionary<string, BookFormat> currentFormats = current?.Formats
            .GroupBy(value => value.Format, StringComparer.Ordinal)
            .Where(value => value.Count() == 1)
            .ToDictionary(value => value.Key, value => value.Single(), StringComparer.Ordinal)
            ?? new Dictionary<string, BookFormat>(StringComparer.Ordinal);
        Dictionary<string, ExpectedFormatState> originals = expected.Formats.ToDictionary(value => value.Format, StringComparer.Ordinal);
        Dictionary<string, FormatFileFingerprint> expectedPost = ExpectedPostFormats(plan, expected.RecordId, journalOperations);
        HashSet<string> inventory = originals.Keys.Concat(expectedPost.Keys).Concat(currentFormats.Keys)
            .ToHashSet(StringComparer.Ordinal);
        List<ReconciledFormat> results = [];

        foreach (string format in inventory.Order(StringComparer.Ordinal))
        {
            originals.TryGetValue(format, out ExpectedFormatState? original);
            currentFormats.TryGetValue(format, out BookFormat? actual);
            bool hasExpectedPost = expectedPost.TryGetValue(format, out FormatFileFingerprint? post);
            SourceOperationProjection? sourceOperation = journalOperations.Values.SingleOrDefault(value =>
                value.Format == format && (value.SourceRecordId == expected.RecordId
                    || value.TargetRecordId == expected.RecordId));
            FormatFileFingerprint? actualFingerprint = actual is { FileStatus: FormatFileStatus.Present or FormatFileStatus.ProjectedPresent }
                ? actual.Fingerprint : null;
            RecoveryReconciliationClassification classification;
            bool preserve = false;
            if (original is not null && actualFingerprint == original.Fingerprint)
                classification = RecoveryReconciliationClassification.AlreadyRestored;
            else if (actualFingerprint is not null && hasExpectedPost && actualFingerprint == post)
                classification = RecoveryReconciliationClassification.ReplacedByExecution;
            else if (actualFingerprint is null)
                classification = RecoveryReconciliationClassification.Missing;
            else if (original is null && !expectedPost.ContainsKey(format))
            {
                classification = RecoveryReconciliationClassification.UnexpectedUniqueContent;
                preserve = true;
                issues.Add(new("RECOVERY.UNEXPECTED_UNIQUE_FORMAT", RecoveryIssueSeverity.Information,
                    $"Logical record {logicalId} / {format}",
                    "A new unique current format will be backed up and preserved.", logicalId, current?.Id, format));
            }
            else
            {
                classification = RecoveryReconciliationClassification.IndependentlyModifiedAfterExecution;
                preserve = true;
                issues.Add(new("RECOVERY.SAME_FORMAT_CONFLICT", RecoveryIssueSeverity.AcknowledgementRequired,
                    $"Logical record {logicalId} / {format}",
                    "The current same-format bytes differ from both verified pre-state and journal post-state; a separate recovery copy is required.",
                    logicalId, current?.Id, format));
            }
            if (sourceOperation is not null)
            {
                if (sourceOperation.State == DurableSourceOperationState.CommandOutcomeUncertain)
                {
                    classification = RecoveryReconciliationClassification.CommandOutcomeUncertain;
                    preserve = actualFingerprint is not null
                        && actualFingerprint != original?.Fingerprint;
                }
                else if (sourceOperation.State == DurableSourceOperationState.DurablyCompleted
                         && actualFingerprint != post)
                {
                    classification = actualFingerprint == original?.Fingerprint
                        ? RecoveryReconciliationClassification.AlreadyRestored
                        : RecoveryReconciliationClassification.CompletedButCurrentStateDiffers;
                    preserve = actualFingerprint is not null
                        && actualFingerprint != original?.Fingerprint;
                }
                else if (sourceOperation.State is DurableSourceOperationState.NotStarted
                             or DurableSourceOperationState.FailedBeforeCompletion
                         && actualFingerprint != original?.Fingerprint)
                {
                    classification = RecoveryReconciliationClassification.NotCompletedButCurrentStateChanged;
                    preserve = actualFingerprint is not null;
                }
            }
            VerifiedRecoverySourceArtifact? artifact = original is null
                ? null : source.FindOriginalFormat(expected.RecordId, format);
            if (original is not null && artifact is null)
                issues.Add(Block("RECOVERY.ORIGINAL_FORMAT_BACKUP_MISSING",
                    $"Original record {expected.RecordId.Value} / {format}",
                    "The required original raw-format backup is missing.", logicalId, current?.Id, format));
            results.Add(new(logicalId, format, classification, original?.Fingerprint,
                expectedPost.TryGetValue(format, out FormatFileFingerprint? postValue) ? postValue : null,
                actualFingerprint, current?.Id, artifact?.RelativePath ?? "not-applicable",
                preserve, sourceOperation?.OperationId));
        }
        return results;
    }

    private static void AddRemovalJournalClassification(
        SourceOperationProjection removal,
        ExpectedRecordState expected,
        CalibreBook? matched,
        List<RecoveryReconciliationClassification> classifications)
    {
        RecoveryReconciliationClassification? value = removal.State switch
        {
            DurableSourceOperationState.CommandOutcomeUncertain =>
                RecoveryReconciliationClassification.CommandOutcomeUncertain,
            DurableSourceOperationState.DurablyCompleted when matched is null =>
                RecoveryReconciliationClassification.CompletedAndMatchesJournalPostState,
            DurableSourceOperationState.DurablyCompleted
                when !IsExactPreState(expected, matched) =>
                RecoveryReconciliationClassification.CompletedButCurrentStateDiffers,
            DurableSourceOperationState.NotStarted or DurableSourceOperationState.FailedBeforeCompletion
                when matched is null =>
                RecoveryReconciliationClassification.NotCompletedButCurrentStateChanged,
            _ => null,
        };
        if (value is not null && !classifications.Contains(value.Value))
            classifications.Add(value.Value);
    }

    private static Dictionary<string, FormatFileFingerprint> ExpectedPostFormats(
        CleanupPlan plan,
        CalibreBookId recordId,
        IReadOnlyDictionary<ExecutionOperationId, SourceOperationProjection> operations)
    {
        ExpectedRecordState expected = plan.Definition.ExpectedLibraryState.Records.Single(value => value.RecordId == recordId);
        Dictionary<string, FormatFileFingerprint> formats = expected.Formats.ToDictionary(
            value => value.Format, value => value.Fingerprint, StringComparer.Ordinal);
        if (recordId != plan.Definition.TargetRecordId) return formats;
        foreach (FormatRetentionInstruction retention in plan.Definition.FormatRetentions)
        {
            SourceOperationProjection? operation = operations.Values.SingleOrDefault(value =>
                value.Kind == ExecutionOperationKind.AddOrReplaceFormat && value.Format == retention.Format);
            if (retention.Mode == FormatRetentionMode.RetainInTarget
                || operation?.State == DurableSourceOperationState.DurablyCompleted)
                formats[retention.Format] = retention.SourceState.Fingerprint;
        }
        return formats;
    }

    private static bool IsExpectedPostState(
        CleanupPlan plan,
        ExpectedRecordState expected,
        CalibreBook current,
        IReadOnlyDictionary<ExecutionOperationId, SourceOperationProjection> operations)
    {
        if (expected.RecordId != plan.Definition.TargetRecordId) return false;
        if (RecoveryRecordFingerprintPolicy.Compute(expected) != RecoveryRecordFingerprintPolicy.Compute(current))
            return false;
        Dictionary<string, FormatFileFingerprint> post = ExpectedPostFormats(plan, expected.RecordId, operations);
        return current.Formats.Count == post.Count
            && current.Formats.All(format => format.Fingerprint is not null
                && post.TryGetValue(format.Format, out FormatFileFingerprint? fingerprint)
                && fingerprint == format.Fingerprint);
    }

    private static bool IsExactPreState(ExpectedRecordState expected, CalibreBook current) =>
        RecoveryRecordFingerprintPolicy.Compute(expected) == RecoveryRecordFingerprintPolicy.Compute(current)
        && expected.Formats.Count == current.Formats.Count
        && expected.Formats.All(format => current.Formats.SingleOrDefault(value => value.Format == format.Format)
            is { FileStatus: FormatFileStatus.Present or FormatFileStatus.ProjectedPresent, Fingerprint: not null } actual
            && actual.Fingerprint == format.Fingerprint);

    private static bool IsIndependentlyModifiedRetainedTarget(
        CleanupPlan plan,
        ExpectedRecordState expected,
        CalibreBook current,
        IReadOnlyDictionary<ExecutionOperationId, SourceOperationProjection> operations)
    {
        if (expected.RecordId != plan.Definition.TargetRecordId
            || !expected.Identifiers.SequenceEqual(
                current.Identifiers.OrderBy(value => value.Type, StringComparer.Ordinal)
                    .ThenBy(value => value.Value, StringComparer.Ordinal)
                    .Select(value => new ExpectedIdentifierState(
                        value.Type, value.Value)))
            || expected.AuthorSort != current.AuthorSort
            || !expected.Authors.Select(value => (value.Name, value.SortName))
                .SequenceEqual(current.Authors.Select(value =>
                    (value.Name, value.SortName))))
            return false;
        Dictionary<string, FormatFileFingerprint> post =
            ExpectedPostFormats(plan, expected.RecordId, operations);
        return current.Formats.Count == post.Count
            && current.Formats.All(format =>
                format is { FileStatus: FormatFileStatus.Present or FormatFileStatus.ProjectedPresent, Fingerprint: not null }
                && post.TryGetValue(format.Format,
                    out FormatFileFingerprint? fingerprint)
                && fingerprint == format.Fingerprint);
    }

    private static bool IsUniqueDifferentIdCandidate(
        ExpectedRecordState expected,
        CalibreBook current)
    {
        bool exactMetadata = RecoveryRecordFingerprintPolicy.Compute(expected)
            == RecoveryRecordFingerprintPolicy.Compute(current);
        bool exactBackedFormat = expected.Formats.Any(format =>
            current.Formats.SingleOrDefault(value => value.Format == format.Format) is
            { FileStatus: FormatFileStatus.Present or FormatFileStatus.ProjectedPresent, Fingerprint: not null } actual
            && actual.Fingerprint == format.Fingerprint);
        return exactMetadata && exactBackedFormat;
    }

    private static IEnumerable<CalibreBook> FindSemanticCandidates(
        ExpectedRecordState expected,
        IEnumerable<CalibreBook> current) =>
        current.Where(book => IsUniqueDifferentIdCandidate(expected, book));

    private static RecoveryIssue Block(
        string code,
        string subject,
        string explanation,
        LogicalRecoveryRecordId? logicalRecordId = null,
        CalibreBookId? currentRecordId = null,
        string? format = null) =>
        new(code, RecoveryIssueSeverity.Blocking, subject, explanation,
            logicalRecordId, currentRecordId, format);
}
