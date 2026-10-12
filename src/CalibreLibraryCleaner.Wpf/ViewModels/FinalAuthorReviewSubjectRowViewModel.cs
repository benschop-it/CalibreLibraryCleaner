using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public enum FinalAuthorReviewFilterMode
{
    All,
    Checked,
    NeedsReview,
    Unresolved,
    Conflicting,
}

public sealed class FinalAuthorReviewSubjectRowViewModel : ObservableObject
{
    private readonly Func<CalibreBookId, bool, Task> _persistApply;
    private FinalAuthorReviewSubject _subject;
    private bool _apply;

    public FinalAuthorReviewSubjectRowViewModel(
        FinalAuthorReviewSubject subject,
        Func<CalibreBookId, bool, Task> persistApply)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _persistApply = persistApply ?? throw new ArgumentNullException(nameof(persistApply));
        _apply = subject.Apply;
        PersistApplyCommand = new AsyncRelayCommand(
            () => _persistApply(_subject.BookId, Apply),
            () => CanApply);
    }

    public CalibreBookId BookId => _subject.BookId;
    public long RecordId => BookId.Value;
    public string CurrentTitle => _subject.CurrentTitle;
    public string CurrentAuthors => Join(_subject.CurrentAuthors.Select(value => value.Name));
    public string CurrentSorts => Join(_subject.CurrentAuthors.Select(value => value.SortName));
    public string ProposedAuthors => Join(_subject.Resolution.ProposedDisplayNames);
    public string ProposedSorts => Join(_subject.Resolution.ProposedSortNames);
    public string Status => _subject.Resolution.Status.ToString();
    public string Source => _subject.Resolution.ProposalSource.ToString();
    public string Protection => _subject.Resolution.CurrentProtection.ToString();
    public string Reasons => Join(_subject.Resolution.ReasonCodes);
    public string Coverage => _subject.EvidenceCoverage.ToString();
    public string Roles => string.Join("; ", _subject.FrontMatterEvidence
        .SelectMany(value => value.Candidates.Select(candidate =>
            $"{candidate.Role}: {Join(candidate.Names)} ({value.Format} {candidate.UnitNumber}:{candidate.LineNumber})"))
        .Take(32));
    public bool NeedsInspection => _subject.NeedsInspection;
    public bool CanApply => _subject.CanApply;
    public bool NeedsReview => CanApply && !_subject.Resolution.SelectedByDefault;
    public bool IsUnresolved => _subject.Resolution.Status == Domain.Metadata.AuthorResolutionStatus.Unresolved;
    public bool IsConflicting => _subject.Resolution.Status == Domain.Metadata.AuthorResolutionStatus.Conflicting;

    public bool Apply
    {
        get => _apply;
        set => SetProperty(ref _apply, value);
    }

    public IAsyncRelayCommand PersistApplyCommand { get; }

    public bool MatchesFilter(FinalAuthorReviewFilterMode mode) => mode switch
    {
        FinalAuthorReviewFilterMode.All => true,
        FinalAuthorReviewFilterMode.Checked => Apply,
        FinalAuthorReviewFilterMode.NeedsReview => NeedsReview,
        FinalAuthorReviewFilterMode.Unresolved => IsUnresolved,
        FinalAuthorReviewFilterMode.Conflicting => IsConflicting,
        _ => false,
    };

    public void Update(FinalAuthorReviewSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.BookId != BookId)
            throw new ArgumentException("The final author review row update is incompatible.");
        _subject = subject;
        _apply = subject.Apply;
        OnPropertyChanged(string.Empty);
        PersistApplyCommand.NotifyCanExecuteChanged();
    }

    private static string Join(IEnumerable<string> values)
    {
        string[] bounded = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(32).ToArray();
        return bounded.Length == 0 ? "None" : string.Join(", ", bounded);
    }
}
