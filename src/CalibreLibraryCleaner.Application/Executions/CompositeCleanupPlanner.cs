using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;

namespace CalibreLibraryCleaner.Application.Executions;

internal sealed record CompositeTransferIntent(
    CalibreBookId SourceRecordId,
    CalibreBookId TargetRecordId,
    BookFormat SourceFormat);

internal sealed record CompositeFormatRemovalIntent(
    CalibreBookId RecordId,
    BookFormat Format);

internal sealed record CompositeCleanupPlan(
    IReadOnlyList<CompositeTransferIntent> Transfers,
    IReadOnlyList<CompositeFormatRemovalIntent> FormatRemovals,
    IReadOnlyList<CalibreBookId> RecordsToRemove,
    CompositeCleanupPlanSummary Summary,
    IReadOnlyList<CompositeCleanupConflict> Conflicts);

internal static class CompositeCleanupPlanner
{
    public static CompositeCleanupPlan Build(
        LibrarySnapshot snapshot,
        IReadOnlyList<ExactDuplicateKeeperSelection> exactSelections,
        IReadOnlyList<MetadataCandidateCleanupSelection> metadataSelections,
        IReadOnlyList<ExpandedCandidateCleanupSelection> expandedSelections)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(exactSelections);
        ArgumentNullException.ThrowIfNull(metadataSelections);
        ArgumentNullException.ThrowIfNull(expandedSelections);
        CompositeRecordConsolidationPlan consolidation = CompositeRecordConsolidationPlanner.Build(
            snapshot, exactSelections, metadataSelections, expandedSelections);
        ExactDuplicateKeeperSelection[] reconciledExactSelections = ReconcileExactSelections(
            snapshot, exactSelections, consolidation);
        List<CompositeCleanupConflict> conflicts = [.. consolidation.Conflicts];
        List<ExecutionIssue> exactIssues = [];
        ExecuteBulkExactDuplicateCleanupUseCase.BulkPlan exact =
            ExecuteBulkExactDuplicateCleanupUseCase.BuildPlan(snapshot, reconciledExactSelections, exactIssues);
        HashSet<CalibreBookId> consolidatedRemovals = consolidation.RecordsToRemove.ToHashSet();

        List<CompositeTransferIntent> transfers =
        [
            .. consolidation.Transfers,
            .. exact.Transfers.Where(value => value.NeedsAdd)
                .Where(value => !consolidatedRemovals.Contains(value.SourceRecordId))
                .Select(value => new CompositeTransferIntent(
                    value.SourceRecordId,
                    consolidation.SurvivorByRecord.GetValueOrDefault(value.TargetRecordId, value.TargetRecordId),
                    value.SourceFormat))
                .Where(value => value.SourceRecordId != value.TargetRecordId)
                .Where(value => !TargetAlreadyHasFormat(snapshot, consolidation, value)),
        ];
        List<CompositeFormatRemovalIntent> removals =
        [
            .. consolidation.FormatRemovals,
            .. exact.FormatRemovals.Select(value => new CompositeFormatRemovalIntent(
                value.RecordId, value.Format))
                .Where(value => !consolidatedRemovals.Contains(value.RecordId)),
        ];
        HashSet<CalibreBookId> recordsToRemove =
        [
            .. consolidation.RecordsToRemove,
            .. exact.RecordsToRemove,
        ];
        HashSet<CalibreBookId> keeperRecords = consolidation.SurvivorByRecord.Values
            .Concat(exactSelections
            .Where(value => !value.Skip && value.KeeperWasOverridden)
            .Select(value => value.RetainedMember.BookId))
            .Concat(metadataSelections.Where(value =>
                    !value.Skip && value.KeeperWasOverridden && value.KeeperBookId is not null)
                .Select(value => value.KeeperBookId!.Value))
            .Concat(expandedSelections.Where(value =>
                    !value.Skip && value.KeeperWasOverridden && value.KeeperBookId is not null)
                .Select(value => value.KeeperBookId!.Value))
            .ToHashSet();

        foreach (CalibreBookId keeper in keeperRecords.Where(recordsToRemove.Contains))
            conflicts.Add(Conflict("COMPOSITE.KEEPER_REMOVED",
                "A record selected as Keep in one view is selected for removal in another view.",
                [keeper]));

        HashSet<FormatKey> retainedAssociations = exactSelections.Where(value =>
            !value.Skip && value.KeeperWasOverridden)
            .Select(value => new FormatKey(
                value.RetainedMember.BookId,
                value.RetainedMember.Format))
            .ToHashSet();
        foreach (CompositeFormatRemovalIntent removal in removals
            .Where(value => retainedAssociations.Contains(new(value.RecordId, value.Format.Format))))
            conflicts.Add(Conflict("COMPOSITE.RETAINED_FORMAT_REMOVED",
                "An exact file selected as Keep is selected for removal by another workflow.",
                [removal.RecordId], removal.Format.Format));

        foreach (IGrouping<FormatKey, CompositeTransferIntent> group in transfers.GroupBy(value =>
            new FormatKey(value.SourceRecordId, value.SourceFormat.Format)))
        {
            CalibreBookId[] targets = group.Select(value => value.TargetRecordId).Distinct().ToArray();
            if (targets.Length > 1)
                conflicts.Add(Conflict("COMPOSITE.SOURCE_MULTIPLE_TARGETS",
                    "One source format is selected for transfer to different keeper records.",
                    [group.Key.RecordId, .. targets], group.Key.Format));
            if (consolidation.SurvivorByRecord.TryGetValue(group.Key.RecordId, out CalibreBookId expected)
                && targets.Any(value => value != expected))
                conflicts.Add(Conflict("COMPOSITE.TRANSFER_KEEPER_MISMATCH",
                    "A source record is assigned to one keeper but a format transfer targets another keeper.",
                    [group.Key.RecordId, expected, .. targets], group.Key.Format));
        }

        foreach (IGrouping<FormatKey, CompositeTransferIntent> group in transfers.GroupBy(value =>
            new FormatKey(value.TargetRecordId, value.SourceFormat.Format)))
        {
            if (group.Select(value => value.SourceFormat.Fingerprint).Distinct().Count() > 1)
                conflicts.Add(Conflict("COMPOSITE.TARGET_FORMAT_CONFLICT",
                    "Different content is selected for the same format on one keeper record.",
                    [group.Key.RecordId, .. group.Select(value => value.SourceRecordId)], group.Key.Format));
        }

        foreach (CompositeTransferIntent transfer in transfers.Where(value =>
            recordsToRemove.Contains(value.TargetRecordId)))
            conflicts.Add(Conflict("COMPOSITE.TRANSFER_TARGET_REMOVED",
                "A transfer target record is also selected for removal.",
                [transfer.SourceRecordId, transfer.TargetRecordId], transfer.SourceFormat.Format));

        Dictionary<FormatKey, CompositeTransferIntent> uniqueTransfers = transfers
            .GroupBy(value => new FormatKey(value.TargetRecordId, value.SourceFormat.Format))
            .Select(value => value.OrderBy(item => item.SourceRecordId.Value).First())
            .ToDictionary(value => new FormatKey(value.TargetRecordId, value.SourceFormat.Format));
        Dictionary<FormatKey, CompositeFormatRemovalIntent> uniqueRemovals = [];
        foreach (IGrouping<FormatKey, CompositeFormatRemovalIntent> group in removals.GroupBy(value =>
            new FormatKey(value.RecordId, value.Format.Format)))
        {
            if (group.Select(value => value.Format.Fingerprint).Distinct().Count() > 1)
                conflicts.Add(Conflict("COMPOSITE.REMOVAL_FINGERPRINT_CONFLICT",
                    "The workflows disagree about the expected fingerprint of a format removal.",
                    [group.Key.RecordId], group.Key.Format));
            uniqueRemovals[group.Key] = group.First();
        }

        foreach (FormatKey key in uniqueTransfers.Keys.Intersect(uniqueRemovals.Keys))
            conflicts.Add(Conflict("COMPOSITE.TARGET_FORMAT_REMOVED",
                "A format transferred to a surviving record is also selected for removal.",
                [key.RecordId], key.Format));

        SimulateFinalInventory(snapshot, uniqueTransfers, uniqueRemovals, recordsToRemove, conflicts);
        CompositeTransferIntent[] orderedTransfers = uniqueTransfers.Values
            .OrderBy(value => value.TargetRecordId.Value)
            .ThenBy(value => value.SourceFormat.Format, StringComparer.Ordinal)
            .ThenBy(value => value.SourceRecordId.Value)
            .ToArray();
        CompositeFormatRemovalIntent[] orderedRemovals = uniqueRemovals.Values
            .OrderBy(value => value.RecordId.Value)
            .ThenBy(value => value.Format.Format, StringComparer.Ordinal)
            .ToArray();
        CalibreBookId[] orderedRecords = recordsToRemove.OrderBy(value => value.Value).ToArray();
        CompositeCleanupPlanSummary summary = new(
            exactSelections.Count(value => !value.Skip),
            consolidation.MetadataSelectionCount,
            consolidation.ExpandedSelectionCount,
            consolidation.SkippedSelectionCount + exactIssues.Count,
            consolidation.ReconciledKeeperCount,
            orderedTransfers.Length,
            orderedRemovals.Length,
            orderedRecords.Length,
            orderedTransfers.Length + orderedRemovals.Length + orderedRecords.Length);
        return new(
            new ReadOnlyCollection<CompositeTransferIntent>(orderedTransfers),
            new ReadOnlyCollection<CompositeFormatRemovalIntent>(orderedRemovals),
            new ReadOnlyCollection<CalibreBookId>(orderedRecords),
            summary,
            new ReadOnlyCollection<CompositeCleanupConflict>(conflicts
                .Distinct().OrderBy(value => value.Code, StringComparer.Ordinal)
                .ThenBy(value => value.RecordIds.Count == 0 ? 0 : value.RecordIds[0].Value).ToArray()));
    }

    private static ExactDuplicateKeeperSelection[] ReconcileExactSelections(
        LibrarySnapshot snapshot,
        IReadOnlyList<ExactDuplicateKeeperSelection> selections,
        CompositeRecordConsolidationPlan consolidation)
    {
        Dictionary<Domain.Duplicates.ExactBinaryDuplicateGroupId, Domain.Duplicates.ExactBinaryDuplicateGroup> groups =
            snapshot.ExactBinaryDuplicateGroups.ToDictionary(value => value.Id);
        HashSet<CalibreBookId> removed = consolidation.RecordsToRemove.ToHashSet();
        return selections.Select(selection =>
        {
            if (selection.Skip
                || selection.KeeperWasOverridden
                || !groups.TryGetValue(selection.GroupId, out Domain.Duplicates.ExactBinaryDuplicateGroup? group))
                return selection;
            Domain.Duplicates.ExactBinaryDuplicateMember? replacement = group.Members
                .Where(value => consolidation.SurvivorByRecord.TryGetValue(value.BookId, out CalibreBookId survivor)
                    && survivor == value.BookId)
                .OrderBy(value => value.BookId.Value)
                .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal)
                .FirstOrDefault();
            replacement ??= group.Members
                .Where(value => !removed.Contains(value.BookId))
                .OrderBy(value => value.BookId.Value)
                .ThenBy(value => value.ExpectedRelativePath, StringComparer.Ordinal)
                .FirstOrDefault();
            return replacement is null ? selection : selection with { RetainedMember = replacement };
        }).ToArray();
    }

    private static bool TargetAlreadyHasFormat(
        LibrarySnapshot snapshot,
        CompositeRecordConsolidationPlan consolidation,
        CompositeTransferIntent transfer) => snapshot.Books
            .Single(value => value.Id == transfer.TargetRecordId)
            .Formats.Any(value => value.Format == transfer.SourceFormat.Format)
        || consolidation.Transfers.Any(value =>
            value.TargetRecordId == transfer.TargetRecordId
            && value.SourceFormat.Format == transfer.SourceFormat.Format);

    private static void SimulateFinalInventory(
        LibrarySnapshot snapshot,
        IReadOnlyDictionary<FormatKey, CompositeTransferIntent> transfers,
        IReadOnlyDictionary<FormatKey, CompositeFormatRemovalIntent> removals,
        IReadOnlySet<CalibreBookId> recordsToRemove,
        List<CompositeCleanupConflict> conflicts)
    {
        Dictionary<FormatKey, FormatFileFingerprint?> inventory = snapshot.Books
            .SelectMany(book => book.Formats.Select(format => new KeyValuePair<FormatKey, FormatFileFingerprint?>(
                new(book.Id, format.Format), format.Fingerprint)))
            .ToDictionary();
        foreach ((FormatKey key, CompositeTransferIntent transfer) in transfers)
        {
            if (inventory.TryGetValue(key, out FormatFileFingerprint? current)
                && current != transfer.SourceFormat.Fingerprint)
                conflicts.Add(Conflict("COMPOSITE.TRANSFER_OVERWRITE_CONFLICT",
                    "A transfer would overwrite different existing content on its target record.",
                    [transfer.SourceRecordId, transfer.TargetRecordId], transfer.SourceFormat.Format));
            else
                inventory[key] = transfer.SourceFormat.Fingerprint;
        }
        foreach ((FormatKey key, CompositeFormatRemovalIntent removal) in removals)
        {
            if (!inventory.TryGetValue(key, out FormatFileFingerprint? current)
                || current != removal.Format.Fingerprint)
                conflicts.Add(Conflict("COMPOSITE.REMOVAL_STATE_CONFLICT",
                    "A planned format removal does not match the initial authoritative inventory.",
                    [key.RecordId], key.Format));
            else
                inventory.Remove(key);
        }
        foreach (CalibreBookId recordId in recordsToRemove)
        {
            string[] remaining = inventory.Keys.Where(value => value.RecordId == recordId)
                .Select(value => value.Format).Order(StringComparer.Ordinal).ToArray();
            if (remaining.Length > 0)
                conflicts.Add(Conflict("COMPOSITE.RECORD_NOT_EMPTY",
                    $"A record selected for removal would still contain formats: {string.Join(", ", remaining)}.",
                    [recordId]));
        }
    }

    private static CompositeCleanupConflict Conflict(
        string code,
        string description,
        IEnumerable<CalibreBookId> records,
        string? format = null) => new(
        code, CompositeCleanupCategory.CrossCategory, description, records, format);

    private readonly record struct FormatKey(CalibreBookId RecordId, string Format);
}
