using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Application.Executions;

internal sealed record CompositeRecordConsolidationPlan(
    IReadOnlyList<CompositeTransferIntent> Transfers,
    IReadOnlyList<CompositeFormatRemovalIntent> FormatRemovals,
    IReadOnlyList<CalibreBookId> RecordsToRemove,
    IReadOnlyDictionary<CalibreBookId, CalibreBookId> SurvivorByRecord,
    int MetadataSelectionCount,
    int ExpandedSelectionCount,
    int SkippedSelectionCount,
    int ReconciledKeeperCount,
    IReadOnlyList<CompositeCleanupConflict> Conflicts);

internal static class CompositeRecordConsolidationPlanner
{
    public static CompositeRecordConsolidationPlan Build(
        LibrarySnapshot snapshot,
        IReadOnlyList<ExactDuplicateKeeperSelection> exactSelections,
        IReadOnlyList<MetadataCandidateCleanupSelection> metadataSelections,
        IReadOnlyList<ExpandedCandidateCleanupSelection> expandedSelections)
    {
        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books.ToDictionary(value => value.Id);
        Dictionary<ExactMetadataDuplicateGroupId, ExactMetadataDuplicateGroup> metadataGroups =
            snapshot.ExactMetadataDuplicateGroups.ToDictionary(value => value.Id);
        Dictionary<WorkLanguageCandidateGroupId, WorkLanguageCandidateGroup> expandedGroups =
            snapshot.WorkLanguageCandidateGroups.ToDictionary(value => value.Id);
        List<SelectedGroup> selectedGroups = [];
        int skipped = 0;

        foreach (MetadataCandidateCleanupSelection selection in metadataSelections.Where(value => !value.Skip))
        {
            if (selection.KeeperBookId is null
                || !metadataGroups.TryGetValue(selection.GroupId, out ExactMetadataDuplicateGroup? group)
                || !group.Members.Contains(selection.KeeperBookId.Value)
                || !HasCompletePhysicalFacts(group.Members, books))
            {
                skipped++;
                continue;
            }
            selectedGroups.Add(new(group.Members, selection.KeeperBookId.Value,
                selection.KeeperWasOverridden, CompositeCleanupCategory.Metadata));
        }

        foreach (ExpandedCandidateCleanupSelection selection in expandedSelections.Where(value => !value.Skip))
        {
            if (selection.KeeperBookId is null
                || !expandedGroups.TryGetValue(selection.GroupId, out WorkLanguageCandidateGroup? group)
                || !group.Members.Contains(selection.KeeperBookId.Value)
                || !HasCompletePhysicalFacts(group.Members, books))
            {
                skipped++;
                continue;
            }
            selectedGroups.Add(new(group.Members, selection.KeeperBookId.Value,
                selection.KeeperWasOverridden, CompositeCleanupCategory.Expanded));
        }

        DisjointSet components = new();
        foreach (SelectedGroup group in selectedGroups)
        {
            CalibreBookId first = group.Members[0];
            components.Add(first);
            foreach (CalibreBookId member in group.Members.Skip(1))
            {
                components.Add(member);
                components.Union(first, member);
            }
        }

        Dictionary<CalibreBookId, ExactDuplicateKeeperSelection[]> exactOverridesByRecord = exactSelections
            .Where(value => !value.Skip && value.KeeperWasOverridden)
            .GroupBy(value => value.RetainedMember.BookId)
            .ToDictionary(value => value.Key, value => value.ToArray());
        List<CompositeTransferIntent> transfers = [];
        List<CompositeFormatRemovalIntent> removals = [];
        List<CalibreBookId> recordsToRemove = [];
        Dictionary<CalibreBookId, CalibreBookId> survivorByRecord = [];
        List<CompositeCleanupConflict> conflicts = [];
        int reconciledKeepers = 0;

        foreach (CalibreBookId[] members in components.Groups())
        {
            HashSet<CalibreBookId> memberSet = members.ToHashSet();
            SelectedGroup[] groups = selectedGroups.Where(value => value.Members.Any(memberSet.Contains)).ToArray();
            CalibreBookId[] explicitKeepers = groups.Where(value => value.KeeperWasOverridden)
                .Select(value => value.SelectedKeeper)
                .Concat(members.Where(exactOverridesByRecord.ContainsKey))
                .Distinct()
                .OrderBy(value => value.Value)
                .ToArray();
            if (explicitKeepers.Length > 1)
            {
                conflicts.Add(new(
                    "COMPOSITE.EXPLICIT_KEEPER_CONFLICT",
                    CompositeCleanupCategory.CrossCategory,
                    "Overlapping groups contain different explicit keeper overrides.",
                    explicitKeepers));
                continue;
            }

            CalibreBookId keeper = explicitKeepers.Length == 1
                ? explicitKeepers[0]
                : ExpandedCandidateRetentionPolicy.SelectKeeper(
                    members.Select(value => books[value]),
                    snapshot.EpubAssessments,
                    snapshot.PdfAssessments);
            reconciledKeepers += groups.Count(value =>
                !value.KeeperWasOverridden && value.SelectedKeeper != keeper);
            foreach (CalibreBookId member in members) survivorByRecord[member] = keeper;

            CalibreBook keeperBook = books[keeper];
            HashSet<string> keeperFormats = keeperBook.Formats.Select(value => value.Format)
                .ToHashSet(StringComparer.Ordinal);
            CalibreBook[] sources = members.Where(value => value != keeper)
                .Select(value => books[value])
                .OrderBy(value => value.Id.Value)
                .ToArray();
            foreach (string format in sources.SelectMany(value => value.Formats)
                .Select(value => value.Format)
                .Distinct(StringComparer.Ordinal)
                .Where(value => !keeperFormats.Contains(value))
                .Order(StringComparer.Ordinal))
            {
                CalibreBookId sourceId = ExpandedCandidateRetentionPolicy.SelectKeeper(
                    sources.Where(value => value.Formats.Any(item => item.Format == format)),
                    snapshot.EpubAssessments,
                    snapshot.PdfAssessments);
                CalibreBook source = books[sourceId];
                transfers.Add(new(source.Id, keeper,
                    source.Formats.Single(value => value.Format == format)));
            }

            foreach (CalibreBook source in sources)
            {
                removals.AddRange(source.Formats.Select(value =>
                    new CompositeFormatRemovalIntent(source.Id, value)));
                recordsToRemove.Add(source.Id);
            }
        }

        return new(
            transfers,
            removals,
            recordsToRemove.Distinct().ToArray(),
            survivorByRecord,
            selectedGroups.Count(value => value.Category == CompositeCleanupCategory.Metadata),
            selectedGroups.Count(value => value.Category == CompositeCleanupCategory.Expanded),
            skipped,
            reconciledKeepers,
            conflicts);
    }

    private static bool HasCompletePhysicalFacts(
        IEnumerable<CalibreBookId> members,
        Dictionary<CalibreBookId, CalibreBook> books) => members
        .SelectMany(value => books[value].Formats)
        .All(value => value.FileStatus == FormatFileStatus.Present && value.Fingerprint is not null);

    private sealed record SelectedGroup(
        IReadOnlyList<CalibreBookId> Members,
        CalibreBookId SelectedKeeper,
        bool KeeperWasOverridden,
        CompositeCleanupCategory Category);

    private sealed class DisjointSet
    {
        private readonly Dictionary<CalibreBookId, CalibreBookId> _parents = [];

        public void Add(CalibreBookId value) => _parents.TryAdd(value, value);

        public void Union(CalibreBookId left, CalibreBookId right)
        {
            CalibreBookId leftRoot = Find(left);
            CalibreBookId rightRoot = Find(right);
            if (leftRoot == rightRoot) return;
            if (leftRoot.Value < rightRoot.Value) _parents[rightRoot] = leftRoot;
            else _parents[leftRoot] = rightRoot;
        }

        public IEnumerable<CalibreBookId[]> Groups() => _parents.Keys
            .GroupBy(Find)
            .Select(value => value.OrderBy(item => item.Value).ToArray())
            .OrderBy(value => value[0].Value);

        private CalibreBookId Find(CalibreBookId value)
        {
            CalibreBookId parent = _parents[value];
            if (parent == value) return value;
            CalibreBookId root = Find(parent);
            _parents[value] = root;
            return root;
        }
    }
}
