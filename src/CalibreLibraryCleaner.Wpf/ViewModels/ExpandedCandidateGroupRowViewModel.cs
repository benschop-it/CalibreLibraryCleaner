using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class ExpandedCandidateGroupRowViewModel : ObservableObject
{
    private ExpandedCandidateMemberRowViewModel? _keeperMember;
    private bool _skip;

    public ExpandedCandidateGroupRowViewModel(
        WorkLanguageCandidateGroup group,
        IReadOnlyDictionary<CalibreBookId, CalibreBook> books,
        ExpandedCandidateRetentionDecision retention)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(books);
        GroupId = group.Id.Value;
        Language = group.Language == "und" ? "Unknown" : group.Language;
        Confidence = group.Confidence.ToString();
        Eligibility = group.CleanupEligibility == WorkLanguageCleanupEligibility.ExplicitKeeperCleanup
            ? "Cleanup eligible"
            : "Review only";
        RecordCount = group.Members.Count;
        AnchorRecordIds = string.Join(", ", group.AnchorMembers.Select(value => value.Value));
        Evidence = string.Join(", ", group.Evidence.Select(value => value.Code));
        Contradictions = group.Contradictions.Count == 0
            ? "None"
            : string.Join(", ", group.Contradictions.Select(value => value.Code));
        ContentEvidence = $"Equivalent {group.ContentComparison.EquivalentPairCount}; high {group.ContentComparison.HighSimilarityPairCount}; ambiguous {group.ContentComparison.AmbiguousPairCount}; different {group.ContentComparison.DifferentPairCount}; unavailable {group.ContentComparison.UnavailablePairCount}";
        Members = new ReadOnlyCollection<ExpandedCandidateMemberRowViewModel>(group.Members
            .Select(member => books.TryGetValue(member, out CalibreBook? book)
                ? new ExpandedCandidateMemberRowViewModel(
                    book,
                    retention.Candidates.Single(value => value.BookId == member))
                : throw new ArgumentException("An expanded candidate member is missing from the snapshot.", nameof(books)))
            .ToArray());
        KeeperMember = Members.Single(value => value.BookId == retention.KeeperBookId.Value);
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

    public ExpandedCandidateMemberRowViewModel? KeeperMember
    {
        get => _keeperMember;
        set
        {
            if (value is not null && !Members.Contains(value))
                throw new ArgumentException("The keeper must belong to this expanded group.", nameof(value));
            if (!SetProperty(ref _keeperMember, value)) return;
            foreach (ExpandedCandidateMemberRowViewModel member in Members) member.IsKeeper = member == value;
            OnPropertyChanged(nameof(KeeperRecordId));
        }
    }

    public long? KeeperRecordId => KeeperMember?.BookId;

    public bool Skip
    {
        get => _skip;
        set => SetProperty(ref _skip, value);
    }
}
