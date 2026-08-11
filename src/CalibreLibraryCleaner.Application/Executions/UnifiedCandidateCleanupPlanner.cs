using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Application.Executions;

public static class UnifiedCandidateCleanupPlanner
{
    public static UnifiedCandidateCleanupPlan Build(
        LibraryState state,
        LibraryStateGenerationId expectedGeneration,
        LibraryStateRevision expectedRevision,
        IReadOnlyList<UnifiedCandidateCleanupSelection> selections,
        List<ExecutionIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(expectedGeneration);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(issues);
        if (!state.IsAuthoritative
            || !state.IsWorkflowCheckpointCurrent
            || state.WorkflowCheckpoint.Phase != LibraryWorkflowPhase.CandidateAnalysisReady
            || state.GenerationId != expectedGeneration
            || state.Revision != expectedRevision)
            throw new InvalidOperationException(
                "Unified Candidate cleanup requires the current authoritative Candidate analysis revision.");
        if (selections.Select(value => value.GroupId).Distinct().Count() != selections.Count)
            throw new ArgumentException("Unified Candidate selections must have unique group IDs.", nameof(selections));

        Dictionary<UnifiedCandidateGroupId, UnifiedCandidateGroup> groups = state.Snapshot.UnifiedCandidateGroups
            .ToDictionary(value => value.Id);
        Dictionary<CalibreBookId, CalibreBook> books = state.Snapshot.Books.ToDictionary(value => value.Id);
        List<UnifiedCandidateTransfer> transfers = [];
        List<UnifiedCandidateFormatRemoval> removals = [];
        List<CalibreBookId> records = [];
        int skipped = 0;
        foreach (UnifiedCandidateCleanupSelection selection in selections
                     .OrderBy(value => value.GroupId.Value, StringComparer.Ordinal))
        {
            if (selection.Skip)
            {
                skipped++;
                continue;
            }
            if (!groups.TryGetValue(selection.GroupId, out UnifiedCandidateGroup? group)
                || !group.Members.Contains(selection.KeeperBookId)
                || !selection.ExpectedMembers.OrderBy(value => value.Value)
                    .SequenceEqual(group.Members.OrderBy(value => value.Value)))
            {
                issues.Add(Warning(
                    "CANDIDATE.GROUP_SELECTION_STALE",
                    "A unified candidate group or keeper changed after review and was skipped."));
                skipped++;
                continue;
            }

            CalibreBook[] members = group.Members.Select(value => books[value]).ToArray();
            if (members.SelectMany(value => value.Formats).Any(format =>
                    format.FileStatus != FormatFileStatus.Present
                    || format.Fingerprint is null
                    || format.Observation is null
                    || string.IsNullOrWhiteSpace(format.StoredFileName)
                    || string.IsNullOrWhiteSpace(format.ExpectedRelativePath)))
            {
                issues.Add(Warning(
                    "CANDIDATE.PHYSICAL_FACTS_INCOMPLETE",
                    "A unified candidate group has missing or unverified physical format facts and was skipped."));
                skipped++;
                continue;
            }

            CalibreBook keeper = books[selection.KeeperBookId];
            CalibreBook[] sources = members.Where(value => value.Id != keeper.Id)
                .OrderBy(value => value.Id.Value).ToArray();
            HashSet<string> keeperFormats = keeper.Formats.Select(value => value.Format)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string format in sources.SelectMany(value => value.Formats)
                         .Select(value => value.Format)
                         .Distinct(StringComparer.Ordinal)
                         .Where(value => !keeperFormats.Contains(value))
                         .Order(StringComparer.Ordinal))
            {
                (CalibreBook Book, BookFormat Format) source = sources
                    .SelectMany(book => book.Formats.Where(value => value.Format == format)
                        .Select(value => (Book: book, Format: value)))
                    .OrderByDescending(value => AssessmentScore(state.Snapshot, value.Book.Id, value.Format))
                    .ThenBy(value => value.Book.Id.Value)
                    .First();
                transfers.Add(new(source.Book.Id, keeper.Id, source.Format));
            }
            foreach (CalibreBook source in sources)
            {
                removals.AddRange(source.Formats.OrderBy(value => value.Format, StringComparer.Ordinal)
                    .Select(value => new UnifiedCandidateFormatRemoval(source.Id, value)));
                records.Add(source.Id);
            }
        }

        UnifiedCandidateTransfer[] orderedTransfers = transfers
            .OrderBy(value => value.TargetRecordId.Value)
            .ThenBy(value => value.SourceFormat.Format, StringComparer.Ordinal)
            .ThenBy(value => value.SourceRecordId.Value).ToArray();
        UnifiedCandidateFormatRemoval[] orderedRemovals = removals
            .OrderBy(value => value.RecordId.Value)
            .ThenBy(value => value.Format.Format, StringComparer.Ordinal).ToArray();
        CalibreBookId[] orderedRecords = records.Distinct().OrderBy(value => value.Value).ToArray();
        ValidateFinalInventory(state.Snapshot, orderedTransfers, orderedRemovals, orderedRecords);
        return new(orderedTransfers, orderedRemovals, orderedRecords, skipped);
    }

    private static void ValidateFinalInventory(
        LibrarySnapshot snapshot,
        IReadOnlyList<UnifiedCandidateTransfer> transfers,
        IReadOnlyList<UnifiedCandidateFormatRemoval> removals,
        IReadOnlyList<CalibreBookId> recordsToRemove)
    {
        Dictionary<CalibreBookId, HashSet<string>> inventory = snapshot.Books.ToDictionary(
            value => value.Id,
            value => value.Formats.Select(format => format.Format).ToHashSet(StringComparer.Ordinal));
        foreach (UnifiedCandidateTransfer transfer in transfers)
        {
            if (!inventory[transfer.SourceRecordId].Contains(transfer.SourceFormat.Format)
                || !inventory[transfer.TargetRecordId].Add(transfer.SourceFormat.Format))
                throw new InvalidOperationException("A unified Candidate transfer conflicts with final inventory.");
        }
        foreach (UnifiedCandidateFormatRemoval removal in removals)
        {
            if (!inventory[removal.RecordId].Remove(removal.Format.Format))
                throw new InvalidOperationException("A unified Candidate format removal is inconsistent.");
        }
        foreach (CalibreBookId record in recordsToRemove)
        {
            if (inventory[record].Count != 0)
                throw new InvalidOperationException("A unified Candidate record removal is not empty.");
        }
    }

    private static int AssessmentScore(LibrarySnapshot snapshot, CalibreBookId bookId, BookFormat format)
    {
        if (format.Format == "EPUB")
            return snapshot.EpubAssessments.FirstOrDefault(value =>
                value.CalibreBookId == bookId
                && string.Equals(value.ExpectedRelativePath, format.ExpectedRelativePath, StringComparison.Ordinal))
                ?.Score?.Value ?? -1;
        if (format.Format == "PDF")
            return snapshot.PdfAssessments.FirstOrDefault(value =>
                value.CalibreBookId == bookId
                && string.Equals(value.ExpectedRelativePath, format.ExpectedRelativePath, StringComparison.Ordinal))
                ?.Score?.Value ?? -1;
        return -1;
    }

    private static ExecutionIssue Warning(string code, string explanation) =>
        new(code, ExecutionIssueSeverity.Warning, explanation);
}
