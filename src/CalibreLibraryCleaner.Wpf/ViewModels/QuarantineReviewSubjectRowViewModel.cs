using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public enum QuarantineReviewFilterMode
{
    All,
    Checked,
    Proposed,
    Manual,
    NeedsReview,
    Repairable,
    Qualified,
}

public sealed class QuarantineReviewSubjectRowViewModel : ObservableObject
{
    private readonly Func<CalibreBookId, bool, Task> _persistInclude;
    private QuarantineReviewSubject _subject;
    private bool _include;

    public QuarantineReviewSubjectRowViewModel(
        QuarantineReviewSubject subject,
        Func<CalibreBookId, bool, Task> persistInclude)
    {
        _subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _persistInclude = persistInclude ?? throw new ArgumentNullException(nameof(persistInclude));
        _include = subject.Include;
        PersistIncludeCommand = new AsyncRelayCommand(
            () => _persistInclude(_subject.BookId, Include));
    }

    public CalibreBookId BookId => _subject.BookId;
    public long RecordId => BookId.Value;
    public string CurrentTitle => _subject.CurrentTitle;
    public string CurrentAuthors => Join(_subject.CurrentAuthors.Select(value => value.Name));
    public string CurrentSorts => Join(_subject.CurrentAuthors.Select(value => value.SortName));
    public string Formats => Join(_subject.Formats.Select(value => value.Format));
    public string Reasons => Join(_subject.Disposition.ReasonCodes);
    public string Provenance => Join(_subject.Disposition.SupportingLabels.Select(value =>
        $"{value.Source}: {value.Value}").Concat(_subject.IdentityCandidates.Select(value =>
        $"Unit {value.UnitNumber}, lines {value.TitleLineNumber}/{value.AuthorLineNumber}: "
        + $"{value.CompleteTitle} / {string.Join(", ", value.Authors)}"
        + (value.ExplicitCredit ? " (explicit title/byline)" : " (uncertain pairing)"))));
    public FinalRecordQualification Qualification => _subject.Qualification;
    public string Coverage => Join(_subject.Disposition.Evidence?.Routes.Select(value =>
        $"{value.Name}: {value.Outcome}") ?? []);
    public string ContentStatus => _subject.Disposition.Evidence?.Content.ToString() ?? "Incomplete";
    public string ManagedLayoutStatus => _subject.Disposition.Evidence?.ManagedLayoutVerified == true
        ? "Verified by the final naming worker" : "Not verified; final managed naming is required";
    public bool IsProposed => _subject.Disposition.ProposedForQuarantine;
    public bool IsManual => _subject.ManuallyIncluded;

    public bool Include
    {
        get => _include;
        set => SetProperty(ref _include, value);
    }

    public IAsyncRelayCommand PersistIncludeCommand { get; }

    public bool MatchesFilter(QuarantineReviewFilterMode mode) => mode switch
    {
        QuarantineReviewFilterMode.All => true,
        QuarantineReviewFilterMode.Checked => Include,
        QuarantineReviewFilterMode.Proposed => IsProposed,
        QuarantineReviewFilterMode.Manual => IsManual,
        QuarantineReviewFilterMode.NeedsReview => Qualification == FinalRecordQualification.NeedsReview,
        QuarantineReviewFilterMode.Repairable => Qualification == FinalRecordQualification.Repairable,
        QuarantineReviewFilterMode.Qualified => Qualification == FinalRecordQualification.Qualified,
        _ => false,
    };

    public void Update(QuarantineReviewSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.BookId != BookId)
            throw new ArgumentException("The quarantine review row update is incompatible.");
        _subject = subject;
        _include = subject.Include;
        OnPropertyChanged(string.Empty);
    }

    private static string Join(IEnumerable<string> values)
    {
        string[] retained = values.Where(value => !string.IsNullOrWhiteSpace(value)).Take(100).ToArray();
        return retained.Length == 0 ? "None" : string.Join(", ", retained);
    }
}
