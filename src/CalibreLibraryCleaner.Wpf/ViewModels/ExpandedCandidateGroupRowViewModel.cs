using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class ExpandedCandidateGroupRowViewModel
{
    public ExpandedCandidateGroupRowViewModel(
        WorkLanguageCandidateGroup group,
        IReadOnlyDictionary<CalibreBookId, CalibreBook> books)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(books);
        GroupId = group.Id.Value;
        Language = group.Language == "und" ? "Unknown" : group.Language;
        Confidence = group.Confidence.ToString();
        Eligibility = "Review only";
        RecordCount = group.Members.Count;
        AnchorRecordIds = string.Join(", ", group.AnchorMembers.Select(value => value.Value));
        Evidence = string.Join(", ", group.Evidence.Select(value => value.Code));
        Contradictions = group.Contradictions.Count == 0
            ? "None"
            : string.Join(", ", group.Contradictions.Select(value => value.Code));
        ContentEvidence = $"Equivalent {group.ContentComparison.EquivalentPairCount}; high {group.ContentComparison.HighSimilarityPairCount}; ambiguous {group.ContentComparison.AmbiguousPairCount}; different {group.ContentComparison.DifferentPairCount}; unavailable {group.ContentComparison.UnavailablePairCount}";
        Members = new ReadOnlyCollection<ExpandedCandidateMemberRowViewModel>(group.Members
            .Select(member => books.TryGetValue(member, out CalibreBook? book)
                ? new ExpandedCandidateMemberRowViewModel(book, group.AnchorMembers.Contains(member))
                : throw new ArgumentException("An expanded candidate member is missing from the snapshot.", nameof(books)))
            .ToArray());
    }

    public string GroupId { get; }
    public string Language { get; }
    public string Confidence { get; }
    public string Eligibility { get; }
    public int RecordCount { get; }
    public string AnchorRecordIds { get; }
    public string Evidence { get; }
    public string Contradictions { get; }
    public string ContentEvidence { get; }
    public IReadOnlyList<ExpandedCandidateMemberRowViewModel> Members { get; }
}
