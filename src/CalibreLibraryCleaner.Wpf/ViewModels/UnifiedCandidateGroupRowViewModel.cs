using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class UnifiedCandidateGroupRowViewModel : ObservableObject
{
    private UnifiedCandidateMemberRowViewModel _keeperMember;
    private bool _skip;

    public UnifiedCandidateGroupRowViewModel(
        UnifiedCandidateGroup group,
        IReadOnlyDictionary<CalibreBookId, CalibreBook> books,
        IEnumerable<EpubAssessment>? epubAssessments = null,
        IEnumerable<PdfAssessment>? pdfAssessments = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(books);
        CandidateGroupId = group.Id;
        MemberBookIds = group.Members;
        GroupId = group.Id.Value;
        Language = group.Language == "und" ? "Unknown" : group.Language;
        Eligibility = group.Classification == UnifiedCandidateClassification.CleanupEligible
            ? "Cleanup eligible"
            : "To be reviewed";
        RecordCount = group.Members.Count;
        EvidenceSources = string.Join(", ", Enum.GetValues<UnifiedCandidateEvidenceSource>()
            .Where(value => value != UnifiedCandidateEvidenceSource.None && group.EvidenceSources.HasFlag(value)));
        Evidence = string.Join(", ", group.Evidence.Select(value => value.Code));
        Contradictions = group.Contradictions.Count == 0
            ? "None"
            : string.Join(", ", group.Contradictions.Select(value => value.Code));
        ReviewFindings = group.ReviewFindings.Count == 0
            ? "None"
            : string.Join(", ", group.ReviewFindings.Select(value => value.Code));
        ContentEvidence = $"Equivalent {group.ContentComparison.EquivalentPairCount}; high {group.ContentComparison.HighSimilarityPairCount}; ambiguous {group.ContentComparison.AmbiguousPairCount}; different {group.ContentComparison.DifferentPairCount}; unavailable {group.ContentComparison.UnavailablePairCount}";
        CalibreBook[] memberBooks = group.Members.Select(member => books.TryGetValue(member, out CalibreBook? book)
            ? book
            : throw new ArgumentException("A unified candidate member is missing from the snapshot.", nameof(books)))
            .ToArray();
        Dictionary<CalibreBookId, ExpandedCandidateRetentionCandidate> retention =
            ExpandedCandidateRetentionPolicy.RankCandidates(memberBooks, epubAssessments, pdfAssessments)
                .ToDictionary(value => value.BookId);
        Members = new ReadOnlyCollection<UnifiedCandidateMemberRowViewModel>(memberBooks
            .Select(book => new UnifiedCandidateMemberRowViewModel(book, retention[book.Id]))
            .ToArray());
        _keeperMember = Members.Single(value => value.BookId == group.GeneratedKeeperBookId.Value);
        foreach (UnifiedCandidateMemberRowViewModel member in Members) member.IsKeeper = member == _keeperMember;
    }

    public string GroupId { get; }
    public UnifiedCandidateGroupId CandidateGroupId { get; }
    public IReadOnlyList<CalibreBookId> MemberBookIds { get; }
    public string Language { get; }
    public string Eligibility { get; }
    public int RecordCount { get; }
    public string EvidenceSources { get; }
    public string Evidence { get; }
    public string Contradictions { get; }
    public string ReviewFindings { get; }
    public string ContentEvidence { get; }
    public IReadOnlyList<UnifiedCandidateMemberRowViewModel> Members { get; }

    public UnifiedCandidateMemberRowViewModel KeeperMember
    {
        get => _keeperMember;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Members.Contains(value))
                throw new ArgumentException("The keeper must belong to this unified group.", nameof(value));
            if (!SetProperty(ref _keeperMember, value)) return;
            KeeperWasOverridden = true;
            foreach (UnifiedCandidateMemberRowViewModel member in Members) member.IsKeeper = member == value;
            OnPropertyChanged(nameof(KeeperRecordId));
            OnPropertyChanged(nameof(KeeperTitle));
            OnPropertyChanged(nameof(KeeperAuthors));
        }
    }

    public long KeeperRecordId => KeeperMember.BookId;
    public CalibreBookId KeeperBookId => new(KeeperMember.BookId);
    public string KeeperTitle => KeeperMember.Title;
    public string KeeperAuthors => KeeperMember.Authors;
    public bool KeeperWasOverridden { get; private set; }

    public bool Skip
    {
        get => _skip;
        set => SetProperty(ref _skip, value);
    }
}
