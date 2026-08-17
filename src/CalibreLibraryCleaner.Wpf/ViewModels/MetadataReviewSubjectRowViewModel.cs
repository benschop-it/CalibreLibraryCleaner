using System.Globalization;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public enum MetadataReviewFilterMode
{
    All,
    AppliedAutomatically,
    NeedsReview,
    Unavailable,
}

public sealed class MetadataReviewSubjectRowViewModel : ObservableObject
{
    private readonly Func<MetadataReviewSubjectId, bool, Task> _persistApply;
    private ReviewedMetadataSubject _reviewed;
    private CalibreBook _currentBook;
    private bool _apply;

    public MetadataReviewSubjectRowViewModel(
        ReviewedMetadataSubject reviewed,
        CalibreBook currentBook,
        Func<MetadataReviewSubjectId, bool, Task> persistApply)
    {
        _reviewed = reviewed ?? throw new ArgumentNullException(nameof(reviewed));
        _currentBook = currentBook ?? throw new ArgumentNullException(nameof(currentBook));
        _persistApply = persistApply ?? throw new ArgumentNullException(nameof(persistApply));
        if (reviewed.Subject.TargetBookId != currentBook.Id)
            throw new ArgumentException("The metadata review current book does not match its target.");
        _apply = reviewed.Apply;
        PersistApplyCommand = new AsyncRelayCommand(
            () => _persistApply(SubjectId, Apply),
            () => CanApply);
    }

    public MetadataReviewSubjectId SubjectId => _reviewed.Subject.Id;
    public string SubjectKind => _reviewed.Subject.UnifiedGroupId is null
        ? "Singleton"
        : $"Unified ({_reviewed.Subject.Members.Count:N0})";
    public long TargetBookId => _reviewed.Subject.TargetBookId.Value;
    public string Confidence => _reviewed.Subject.Proposal.Confidence.ToString();
    public bool IsAutomaticConfidence => _reviewed.Subject.Proposal.Confidence
        is FusedEditionMetadataConfidence.High or FusedEditionMetadataConfidence.Medium;
    public bool NeedsReview => _reviewed.Subject.Proposal.Confidence == FusedEditionMetadataConfidence.Low;
    public bool IsUnavailable => _reviewed.Subject.Proposal.Confidence == FusedEditionMetadataConfidence.Unavailable;
    public bool CanApply => !IsUnavailable;
    public bool IsOverride => _reviewed.IsOverride;
    public string Reasons => string.Join(", ", _reviewed.Subject.Proposal.ReasonCodes);
    public string Provenance => FormatProvenance(_reviewed.Subject.Proposal);

    public bool Apply
    {
        get => _apply;
        set => SetProperty(ref _apply, value);
    }

    public IAsyncRelayCommand PersistApplyCommand { get; }

    public bool MatchesFilter(MetadataReviewFilterMode mode) => mode switch
    {
        MetadataReviewFilterMode.All => true,
        MetadataReviewFilterMode.AppliedAutomatically => IsAutomaticConfidence && Apply,
        MetadataReviewFilterMode.NeedsReview => NeedsReview,
        MetadataReviewFilterMode.Unavailable => IsUnavailable,
        _ => false,
    };

    public string CurrentTitle => _currentBook.Title;
    public string CurrentAuthors => Join(_currentBook.Authors.Select(value => value.Name));
    public string CurrentIdentifiers => Join(_currentBook.Identifiers.Select(value => $"{value.Type}:{value.Value}"));
    public string CurrentPublisher => Value(_currentBook.PublicationMetadata.Publisher);
    public string CurrentPublicationDate => _currentBook.PublicationMetadata.PublicationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        ?? "Unknown";
    public string CurrentLanguages => Join(_currentBook.PublicationMetadata.Languages);
    public string CurrentSeries => Series(
        _currentBook.PublicationMetadata.Series,
        _currentBook.PublicationMetadata.SeriesIndex);
    public string CurrentCover => _currentBook.PublicationMetadata.HasCover ? "Available" : "None";

    public string ProposedTitle => Value(Candidate?.Title);
    public string ProposedAuthors => Join(Candidate?.Authors ?? []);
    public string ProposedIdentifiers => Join(Candidate?.Identifiers.Select(value => value.CanonicalValue) ?? []);
    public string ProposedPublisher => Value(Candidate?.Publisher);
    public string ProposedPublicationDate => Date(Candidate?.PublicationDate);
    public string ProposedLanguages => Join(Candidate?.Languages ?? []);
    public string ProposedSeries => Series(Candidate?.Series, Candidate?.SeriesIndex);
    public string ProposedCover => Candidate?.Cover is null
        ? "None"
        : $"Available ({Candidate.Cover.SourceId})";

    private EditionMetadataCandidate? Candidate => _reviewed.Subject.Proposal.Candidate;

    public void Update(ReviewedMetadataSubject reviewed, CalibreBook currentBook)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        ArgumentNullException.ThrowIfNull(currentBook);
        if (reviewed.Subject.Id != SubjectId || reviewed.Subject.TargetBookId != currentBook.Id)
            throw new ArgumentException("The metadata review row update is incompatible.");
        _reviewed = reviewed;
        _currentBook = currentBook;
        _apply = reviewed.Apply;
        OnPropertyChanged(string.Empty);
        PersistApplyCommand.NotifyCanExecuteChanged();
    }

    private static string FormatProvenance(FusedEditionMetadataProposal proposal)
    {
        if (proposal.Candidate is null || proposal.PrimaryProvider is null) return "Unavailable";
        string primary = $"Primary: {proposal.PrimaryProvider.Id} {proposal.PrimaryProvider.Version} / {proposal.Candidate.EditionId}";
        string providers = string.Join("; ", proposal.ProviderProposals
            .Where(value => value.Candidate is not null)
            .Select(value => $"{value.Provider.Id} {value.Provider.Version} / {value.Candidate!.EditionId} / {value.RetrievedAtUtc:u}")
            .Distinct(StringComparer.Ordinal)
            .Take(16));
        return providers.Length == 0 ? primary : primary + "; Sources: " + providers;
    }

    private static string Date(EditionPublicationDate? value) => value is null
        ? "Unknown"
        : value.Day is not null
            ? $"{value.Year:D4}-{value.Month!.Value:D2}-{value.Day.Value:D2}"
            : value.Month is not null
                ? $"{value.Year:D4}-{value.Month.Value:D2}"
                : value.Year.ToString("D4", CultureInfo.InvariantCulture);

    private static string Series(string? value, decimal? index) => string.IsNullOrWhiteSpace(value)
        ? "None"
        : index is null
            ? value
            : $"{value} #{index.Value.ToString(CultureInfo.InvariantCulture)}";

    private static string Join(IEnumerable<string> values)
    {
        string[] bounded = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).Take(32).ToArray();
        return bounded.Length == 0 ? "None" : string.Join(", ", bounded);
    }

    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
}
