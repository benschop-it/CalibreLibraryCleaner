using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ValidateLibraryUseCase _validateLibrary;
    private readonly ScanLibraryUseCase _scanLibrary;
    private readonly ILibraryFolderPicker _folderPicker;
    private readonly ExportRecommendationsUseCase? _exportRecommendations;
    private readonly IRecommendationExportFilePicker? _exportFilePicker;
    private readonly IClock? _clock;
    private readonly BulkObservableCollection<BookRowViewModel> _books = [];
    private readonly BulkObservableCollection<ExactDuplicateGroupRowViewModel> _exactDuplicateGroups = [];
    private readonly BulkObservableCollection<MetadataDuplicateGroupRowViewModel> _metadataDuplicateGroups = [];
    private readonly BulkObservableCollection<EpubAssessmentRowViewModel> _epubAssessments = [];
    private readonly BulkObservableCollection<EpubAssessmentFindingRowViewModel> _epubFindings = [];
    private readonly Dictionary<RecommendationReviewKey, ReviewedConsolidationRecommendation> _recommendationReviews = [];
    private IReadOnlyList<MetadataDuplicateGroupRowViewModel> _allMetadataDuplicateGroups = [];
    private CancellationTokenSource? _scanCancellation;
    private string _selectedLibraryPath = string.Empty;
    private string _statusMessage = "Choose a Calibre library folder.";
    private string _errorMessage = string.Empty;
    private string _errorAction = string.Empty;
    private string _exactDuplicateSummary = "No exact file duplicate groups have been found.";
    private string _metadataDuplicateSummary = "No exact metadata candidate groups have been found.";
    private string _metadataDuplicateFilterText = string.Empty;
    private MetadataDuplicateFilterMode _metadataDuplicateFilterMode;
    private bool _isBusy;
    private double _progressPercentage;
    private bool _isProgressIndeterminate;
    private BookRowViewModel? _selectedBook;
    private ExactDuplicateGroupRowViewModel? _selectedExactDuplicateGroup;
    private MetadataDuplicateGroupRowViewModel? _selectedMetadataDuplicateGroup;
    private EpubAssessmentRowViewModel? _selectedEpubAssessment;
    private EpubFindingFilterMode _epubFindingFilterMode;
    private string? _currentLibraryUuid;
    private LibrarySnapshot? _currentSnapshot;

    public MainWindowViewModel(
        ValidateLibraryUseCase validateLibrary,
        ScanLibraryUseCase scanLibrary,
        ILibraryFolderPicker folderPicker,
        ExportRecommendationsUseCase? exportRecommendations = null,
        IRecommendationExportFilePicker? exportFilePicker = null,
        IClock? clock = null,
        CleanupPlanWorkspaceViewModel? cleanupPlans = null,
        CleanupExecutionWorkspaceViewModel? cleanupExecutions = null)
    {
        _validateLibrary = validateLibrary;
        _scanLibrary = scanLibrary;
        _folderPicker = folderPicker;
        _exportRecommendations = exportRecommendations;
        _exportFilePicker = exportFilePicker;
        _clock = clock;
        CleanupPlans = cleanupPlans;
        CleanupExecutions = cleanupExecutions;
        Books = new ReadOnlyObservableCollection<BookRowViewModel>(_books);
        ExactDuplicateGroups = new ReadOnlyObservableCollection<ExactDuplicateGroupRowViewModel>(_exactDuplicateGroups);
        MetadataDuplicateGroups = new ReadOnlyObservableCollection<MetadataDuplicateGroupRowViewModel>(
            _metadataDuplicateGroups);
        EpubAssessments = new ReadOnlyObservableCollection<EpubAssessmentRowViewModel>(_epubAssessments);
        EpubFindings = new ReadOnlyObservableCollection<EpubAssessmentFindingRowViewModel>(_epubFindings);
        SelectLibraryCommand = new AsyncRelayCommand(SelectLibraryAsync, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedLibraryPath));
        CancelCommand = new RelayCommand(
            CancelScan,
            () => IsBusy && _scanCancellation is { IsCancellationRequested: false });
        NextMetadataDuplicateGroupCommand = new RelayCommand(
            () => MoveMetadataSelection(1),
            () => !IsBusy && _metadataDuplicateGroups.Count > 0);
        PreviousMetadataDuplicateGroupCommand = new RelayCommand(
            () => MoveMetadataSelection(-1),
            () => !IsBusy && _metadataDuplicateGroups.Count > 0);
        ToggleMetadataDuplicateDeferredCommand = new RelayCommand(
            ToggleMetadataDuplicateDeferred,
            () => !IsBusy && SelectedMetadataDuplicateGroup is not null && _currentLibraryUuid is not null);
        AcceptRecommendationCommand = new RelayCommand(() => ApplySelectedReview(RecommendationReviewStatus.Accepted), CanReviewSelected);
        SaveManualAdjustmentCommand = new RelayCommand(SaveManualAdjustment, CanReviewSelected);
        KeepSeparateRecommendationCommand = new RelayCommand(() => ApplySelectedReview(RecommendationReviewStatus.KeepSeparate), CanReviewSelected);
        MarkNotDuplicatesCommand = new RelayCommand(() => ApplySelectedReview(RecommendationReviewStatus.NotDuplicates), CanReviewSelected);
        ResetRecommendationCommand = new RelayCommand(ResetSelectedReview, CanReviewSelected);
        ExportRecommendationsCommand = new AsyncRelayCommand(ExportRecommendationsAsync, () => !IsBusy && _currentSnapshot is not null && _exportRecommendations is not null && _exportFilePicker is not null);
    }

    public string SelectedLibraryPath
    {
        get => _selectedLibraryPath;
        private set
        {
            if (SetProperty(ref _selectedLibraryPath, value))
            {
                ScanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string ErrorAction
    {
        get => _errorAction;
        private set => SetProperty(ref _errorAction, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SelectLibraryCommand.NotifyCanExecuteChanged();
                ScanCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
                NextMetadataDuplicateGroupCommand.NotifyCanExecuteChanged();
                PreviousMetadataDuplicateGroupCommand.NotifyCanExecuteChanged();
                ToggleMetadataDuplicateDeferredCommand.NotifyCanExecuteChanged();
                NotifyRecommendationCommands();
            }
        }
    }

    public double ProgressPercentage
    {
        get => _progressPercentage;
        private set => SetProperty(ref _progressPercentage, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public ReadOnlyObservableCollection<BookRowViewModel> Books { get; }

    public ReadOnlyObservableCollection<ExactDuplicateGroupRowViewModel> ExactDuplicateGroups { get; }

    public ReadOnlyObservableCollection<MetadataDuplicateGroupRowViewModel> MetadataDuplicateGroups { get; }

    public ReadOnlyObservableCollection<EpubAssessmentRowViewModel> EpubAssessments { get; }

    public ReadOnlyObservableCollection<EpubAssessmentFindingRowViewModel> EpubFindings { get; }

    public CleanupPlanWorkspaceViewModel? CleanupPlans { get; }

    public CleanupExecutionWorkspaceViewModel? CleanupExecutions { get; }

    public IReadOnlyList<EpubFindingFilterMode> EpubFindingFilterModes { get; } = Enum.GetValues<EpubFindingFilterMode>();

    public IReadOnlyList<MetadataDuplicateFilterMode> MetadataDuplicateFilterModes { get; } =
        Enum.GetValues<MetadataDuplicateFilterMode>();

    public string ExactDuplicateSummary
    {
        get => _exactDuplicateSummary;
        private set => SetProperty(ref _exactDuplicateSummary, value);
    }

    public string MetadataDuplicateSummary
    {
        get => _metadataDuplicateSummary;
        private set => SetProperty(ref _metadataDuplicateSummary, value);
    }

    public string MetadataDuplicateFilterText
    {
        get => _metadataDuplicateFilterText;
        set
        {
            if (SetProperty(ref _metadataDuplicateFilterText, value ?? string.Empty))
            {
                ApplyMetadataDuplicateFilter();
            }
        }
    }

    public MetadataDuplicateFilterMode MetadataDuplicateFilterMode
    {
        get => _metadataDuplicateFilterMode;
        set
        {
            if (SetProperty(ref _metadataDuplicateFilterMode, value))
            {
                ApplyMetadataDuplicateFilter();
            }
        }
    }

    public BookRowViewModel? SelectedBook
    {
        get => _selectedBook;
        set
        {
            if (SetProperty(ref _selectedBook, value))
            {
                OnPropertyChanged(nameof(SelectedFormats));
            }
        }
    }

    public IReadOnlyList<FormatRowViewModel> SelectedFormats => SelectedBook?.Formats ?? [];

    public ExactDuplicateGroupRowViewModel? SelectedExactDuplicateGroup
    {
        get => _selectedExactDuplicateGroup;
        set
        {
            if (SetProperty(ref _selectedExactDuplicateGroup, value))
            {
                OnPropertyChanged(nameof(SelectedExactDuplicateMembers));
            }
        }
    }

    public IReadOnlyList<ExactDuplicateMemberRowViewModel> SelectedExactDuplicateMembers =>
        SelectedExactDuplicateGroup?.Members ?? [];

    public MetadataDuplicateGroupRowViewModel? SelectedMetadataDuplicateGroup
    {
        get => _selectedMetadataDuplicateGroup;
        set
        {
            if (SetProperty(ref _selectedMetadataDuplicateGroup, value))
            {
                OnPropertyChanged(nameof(SelectedMetadataDuplicateMembers));
                OnPropertyChanged(nameof(MetadataDeferAction));
                OnPropertyChanged(nameof(SelectedRecommendationFormats));
                OnPropertyChanged(nameof(SelectedRecommendationReasons));
                OnPropertyChanged(nameof(SelectedRecommendationWarnings));
                OnPropertyChanged(nameof(SelectedMetadataSourceOptions));
                OnPropertyChanged(nameof(ReviewedMetadataSource));
                OnPropertyChanged(nameof(StaleOverrideSummary));
                ToggleMetadataDuplicateDeferredCommand.NotifyCanExecuteChanged();
                NotifyRecommendationCommands();
                CleanupPlans?.UpdateContext(_currentSnapshot, value?.Reviewed);
            }
        }
    }

    public IReadOnlyList<MetadataDuplicateMemberRowViewModel> SelectedMetadataDuplicateMembers =>
        SelectedMetadataDuplicateGroup?.Members ?? [];

    public IReadOnlyList<RecommendationFormatRowViewModel> SelectedRecommendationFormats =>
        SelectedMetadataDuplicateGroup?.FormatRows ?? [];

    public IReadOnlyList<RecommendationReasonRowViewModel> SelectedRecommendationReasons =>
        SelectedMetadataDuplicateGroup?.ReasonRows ?? [];

    public IReadOnlyList<RecommendationWarningRowViewModel> SelectedRecommendationWarnings =>
        SelectedMetadataDuplicateGroup?.WarningRows ?? [];

    public string StaleOverrideSummary => SelectedMetadataDuplicateGroup?.StaleOverrideSummary ?? string.Empty;

    public IReadOnlyList<RecommendationSourceOptionViewModel> SelectedMetadataSourceOptions =>
        SelectedMetadataDuplicateGroup?.MetadataSourceOptions ?? [];

    public RecommendationSourceOptionViewModel? ReviewedMetadataSource
    {
        get => SelectedMetadataDuplicateGroup?.ReviewedMetadataSource;
        set
        {
            if (SelectedMetadataDuplicateGroup is not null)
            {
                SelectedMetadataDuplicateGroup.ReviewedMetadataSource = value;
                OnPropertyChanged();
            }
        }
    }

    public EpubAssessmentRowViewModel? SelectedEpubAssessment
    {
        get => _selectedEpubAssessment;
        set
        {
            if (SetProperty(ref _selectedEpubAssessment, value))
            {
                ApplyEpubFindingFilter();
                OnPropertyChanged(nameof(SelectedEpubFeatureSummary));
                OnPropertyChanged(nameof(EpubDisqualificationMessage));
            }
        }
    }

    public EpubFindingFilterMode EpubFindingFilterMode
    {
        get => _epubFindingFilterMode;
        set { if (SetProperty(ref _epubFindingFilterMode, value)) ApplyEpubFindingFilter(); }
    }

    public string SelectedEpubFeatureSummary => SelectedEpubAssessment?.FeatureSummary ?? "Select an EPUB assessment to view bounded format facts.";

    public string EpubDisqualificationMessage => SelectedEpubAssessment?.Status == "Disqualified"
        ? "Not scored — disqualified. See the disqualifying finding below."
        : string.Empty;

    public string MetadataDeferAction => SelectedMetadataDuplicateGroup?.IsDeferred == true
        ? "_Restore"
        : "_Defer";

    public IAsyncRelayCommand SelectLibraryCommand { get; }

    public IAsyncRelayCommand ScanCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IRelayCommand NextMetadataDuplicateGroupCommand { get; }

    public IRelayCommand PreviousMetadataDuplicateGroupCommand { get; }

    public IRelayCommand ToggleMetadataDuplicateDeferredCommand { get; }

    public IRelayCommand AcceptRecommendationCommand { get; }

    public IRelayCommand SaveManualAdjustmentCommand { get; }

    public IRelayCommand KeepSeparateRecommendationCommand { get; }

    public IRelayCommand MarkNotDuplicatesCommand { get; }

    public IRelayCommand ResetRecommendationCommand { get; }

    public IAsyncRelayCommand ExportRecommendationsCommand { get; }

    public void Dispose()
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
    }

    private async Task SelectLibraryAsync()
    {
        string? selected = _folderPicker.PickFolder(SelectedLibraryPath);
        if (selected is null)
        {
            return;
        }

        SelectedLibraryPath = selected;
        ClearError();
        LibraryValidationOutcome outcome = await _validateLibrary
            .ExecuteAsync(selected, CancellationToken.None)
            .ConfigureAwait(true);
        if (outcome.IsSuccess)
        {
            StatusMessage = "Library folder is valid. Select Scan to load it.";
        }
        else
        {
            ShowError(outcome.Error!);
            StatusMessage = "Library validation failed.";
        }
    }

    private async Task ScanAsync()
    {
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        IsBusy = true;
        ClearError();
        ProgressPercentage = 0;
        IsProgressIndeterminate = true;
        CoalescingProgress progress = new(UpdateProgress);

        try
        {
            LibraryScanOutcome outcome = await _scanLibrary
                .ExecuteAsync(SelectedLibraryPath, progress, _scanCancellation.Token)
                .ConfigureAwait(true);
            progress.Complete();
            if (outcome.IsSuccess)
            {
                SnapshotPresentation presentation = await Task.Run(
                        () => CreatePresentation(outcome.Snapshot!, _scanCancellation.Token),
                        _scanCancellation.Token)
                    .ConfigureAwait(true);
                ApplySnapshot(outcome.Snapshot!, presentation);
            }
            else
            {
                ShowError(outcome.Error!);
                StatusMessage = "Library scan failed. Previous results, if any, were not replaced.";
            }
        }
        catch (OperationCanceledException)
        {
            progress.Complete();
            StatusMessage = "Scan canceled. Previous results, if any, were not replaced.";
        }
        finally
        {
            progress.Complete();
            IsBusy = false;
            _scanCancellation.Dispose();
            _scanCancellation = null;
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private void CancelScan()
    {
        _scanCancellation?.Cancel();
        CancelCommand.NotifyCanExecuteChanged();
        StatusMessage = "Scan canceled. Waiting for the current read to stop...";
    }

    private void UpdateProgress(LibraryScanProgress progress)
    {
        StatusMessage = progress.Message;
        IsProgressIndeterminate = progress.TotalUnits is null;
        ProgressPercentage = progress.TotalUnits is > 0
            ? 100d * progress.CompletedUnits / progress.TotalUnits.Value
            : progress.Phase == LibraryScanPhase.Completed ? 100 : 0;
    }

    private static SnapshotPresentation CreatePresentation(
        LibrarySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        BookRowViewModel[] books = new BookRowViewModel[snapshot.Books.Count];
        Dictionary<CalibreBookId, CalibreBook> booksById = new(snapshot.Books.Count);
        for (int index = 0; index < snapshot.Books.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CalibreBook book = snapshot.Books[index];
            books[index] = new(book);
            booksById.Add(book.Id, book);
        }

        ExactDuplicateGroupRowViewModel[] groups = new ExactDuplicateGroupRowViewModel[
            snapshot.ExactBinaryDuplicateGroups.Count];
        for (int index = 0; index < snapshot.ExactBinaryDuplicateGroups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            groups[index] = new(snapshot.ExactBinaryDuplicateGroups[index], booksById);
        }

        Dictionary<ExactMetadataDuplicateGroupId, ConsolidationRecommendation> recommendations = snapshot.ConsolidationRecommendations
            .ToDictionary(value => value.GroupId);
        MetadataDuplicateGroupRowViewModel[] metadataGroups = new MetadataDuplicateGroupRowViewModel[
            snapshot.ExactMetadataDuplicateGroups.Count];
        for (int index = 0; index < snapshot.ExactMetadataDuplicateGroups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExactMetadataDuplicateGroup metadataGroup = snapshot.ExactMetadataDuplicateGroups[index];
            recommendations.TryGetValue(metadataGroup.Id, out ConsolidationRecommendation? recommendation);
            metadataGroups[index] = new(metadataGroup, booksById, recommendation);
        }

        EpubAssessmentRowViewModel[] epubAssessments = new EpubAssessmentRowViewModel[snapshot.EpubAssessments.Count];
        for (int index = 0; index < snapshot.EpubAssessments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Domain.Assessments.FormatAssessment assessment = snapshot.EpubAssessments[index];
            booksById.TryGetValue(assessment.CalibreBookId, out CalibreBook? book);
            epubAssessments[index] = new(assessment, book);
        }

        int missingCount = 0;
        foreach (Domain.Findings.LibraryFinding finding in snapshot.Findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (finding.Code == "FORMAT_FILE_MISSING")
            {
                missingCount++;
            }
        }

        return new(books, groups, metadataGroups, epubAssessments, missingCount);
    }

    private void ApplySnapshot(LibrarySnapshot snapshot, SnapshotPresentation presentation)
    {
        _books.ReplaceAll(presentation.Books);
        SelectedBook = _books.FirstOrDefault();
        _exactDuplicateGroups.ReplaceAll(presentation.Groups);
        SelectedExactDuplicateGroup = _exactDuplicateGroups.FirstOrDefault();
        ExactDuplicateSummary = presentation.Groups.Count == 0
            ? "No exact file duplicate groups were found in this scan."
            : presentation.Groups.Count == 1
                ? "1 exact file duplicate group was found."
                : $"{presentation.Groups.Count} exact file duplicate groups were found.";
        _currentLibraryUuid = snapshot.Identity.CalibreLibraryUuid;
        foreach (MetadataDuplicateGroupRowViewModel group in presentation.MetadataGroups)
        {
            if (group.Recommendation is null)
            {
                continue;
            }

            RecommendationReviewKey key = new(_currentLibraryUuid, group.GroupId);
            ReviewedConsolidationRecommendation reviewed = _recommendationReviews.TryGetValue(key, out ReviewedConsolidationRecommendation? previous)
                ? RecommendationReviewStalenessEvaluator.Reconcile(group.Recommendation, previous)
                : ApplyRecommendationOverrideUseCase.Reset(group.Recommendation);
            group.SetReviewed(reviewed);
            if (previous is not null)
            {
                _recommendationReviews[key] = reviewed;
            }
        }

        _currentSnapshot = snapshot;
        CleanupPlans?.ReconcileAfterSuccessfulScan(snapshot);
        CleanupExecutions?.UpdateSnapshot(snapshot);
        _allMetadataDuplicateGroups = presentation.MetadataGroups;
        ApplyMetadataDuplicateFilter();
        _epubAssessments.ReplaceAll(presentation.EpubAssessments);
        SelectedEpubAssessment = _epubAssessments.FirstOrDefault();
        StatusMessage = snapshot.Books.Count == 0
            ? "Scan complete. The library contains no books."
            : $"Scan complete: {snapshot.Books.Count} books, {snapshot.ExactBinaryDuplicateGroups.Count} exact file duplicate groups, {snapshot.ExactMetadataDuplicateGroups.Count} exact metadata candidate groups, {presentation.MissingCount} missing format files.";
        IsProgressIndeterminate = false;
        ProgressPercentage = 100;
        ExportRecommendationsCommand.NotifyCanExecuteChanged();
    }

    private void ClearError()
    {
        ErrorMessage = string.Empty;
        ErrorAction = string.Empty;
    }

    private void ShowError(LibraryError error)
    {
        ErrorMessage = error.Message;
        ErrorAction = error.SuggestedAction;
    }

    private void ApplyMetadataDuplicateFilter()
    {
        ExactMetadataDuplicateGroupId? selectedId = SelectedMetadataDuplicateGroup?.GroupId;
        MetadataDuplicateGroupRowViewModel[] visible = _allMetadataDuplicateGroups
            .Where(group => MetadataDuplicateFilterMode switch
            {
                MetadataDuplicateFilterMode.All => true,
                MetadataDuplicateFilterMode.Active => !group.IsDeferred,
                MetadataDuplicateFilterMode.Deferred => group.IsDeferred,
                _ => false,
            })
            .Where(group => string.IsNullOrWhiteSpace(MetadataDuplicateFilterText) ||
                            group.SearchText.Contains(MetadataDuplicateFilterText.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        _metadataDuplicateGroups.ReplaceAll(visible);
        SelectedMetadataDuplicateGroup = selectedId is null
            ? _metadataDuplicateGroups.FirstOrDefault()
            : _metadataDuplicateGroups.FirstOrDefault(group => group.GroupId == selectedId.Value) ??
              _metadataDuplicateGroups.FirstOrDefault();

        int deferredCount = _allMetadataDuplicateGroups.Count(group => group.IsDeferred);
        MetadataDuplicateSummary = _allMetadataDuplicateGroups.Count == 0
            ? "No exact metadata candidate groups were found in this scan."
            : visible.Length == 0
                ? $"No metadata candidate groups match the current filter (0 of {_allMetadataDuplicateGroups.Count} visible; {deferredCount} deferred)."
                : $"{visible.Length} of {_allMetadataDuplicateGroups.Count} metadata candidate groups visible; {deferredCount} deferred.";
        NextMetadataDuplicateGroupCommand.NotifyCanExecuteChanged();
        PreviousMetadataDuplicateGroupCommand.NotifyCanExecuteChanged();
        ToggleMetadataDuplicateDeferredCommand.NotifyCanExecuteChanged();
    }

    private void MoveMetadataSelection(int offset)
    {
        if (_metadataDuplicateGroups.Count == 0)
        {
            return;
        }

        int currentIndex = SelectedMetadataDuplicateGroup is null
            ? 0
            : _metadataDuplicateGroups.IndexOf(SelectedMetadataDuplicateGroup);
        int nextIndex = (currentIndex + offset + _metadataDuplicateGroups.Count) % _metadataDuplicateGroups.Count;
        SelectedMetadataDuplicateGroup = _metadataDuplicateGroups[nextIndex];
    }

    private void ToggleMetadataDuplicateDeferred()
    {
        if (SelectedMetadataDuplicateGroup?.Recommendation is null || _currentLibraryUuid is null)
        {
            return;
        }

        if (SelectedMetadataDuplicateGroup.IsDeferred)
        {
            ResetSelectedReview();
        }
        else
        {
            SaveReviewAdjustment(RecommendationReviewStatus.Deferred);
        }

        OnPropertyChanged(nameof(MetadataDeferAction));
        ApplyMetadataDuplicateFilter();
    }

    private bool CanReviewSelected() => !IsBusy
        && SelectedMetadataDuplicateGroup?.Recommendation is not null
        && _currentLibraryUuid is not null;

    private void ApplySelectedReview(RecommendationReviewStatus status)
    {
        if (SelectedMetadataDuplicateGroup?.Recommendation is not { } generated || _currentLibraryUuid is null)
        {
            return;
        }

        IReadOnlyList<CalibreBookId> separate = status == RecommendationReviewStatus.KeepSeparate
            ? generated.MemberIds
            : [];
        UserRecommendationOverride proposed = new(
            generated.ModelVersion,
            generated.InputVersion,
            status,
            _clock?.GetUtcNow() ?? DateTimeOffset.UtcNow,
            retainedSeparateBookIds: separate);
        ApplyOverride(proposed);
    }

    private void SaveManualAdjustment() => SaveReviewAdjustment(RecommendationReviewStatus.ManuallyAdjusted);

    private void SaveReviewAdjustment(RecommendationReviewStatus requestedStatus)
    {
        if (SelectedMetadataDuplicateGroup?.Recommendation is not { } generated)
        {
            return;
        }

        List<FormatRecommendationOverride> formatOverrides = [];
        foreach (RecommendationFormatRowViewModel row in SelectedMetadataDuplicateGroup.FormatRows)
        {
            RecommendationSourceOptionViewModel? selected = row.ReviewedSource;
            FormatSourceSelection original = generated.FormatSelections.Single(value => value.Format == row.Format);
            if (selected?.Action == "MarkUnresolved")
            {
                if (original.ResolutionStatus != FormatResolutionStatus.UnresolvedConflict) formatOverrides.Add(new(row.Format, FormatOverrideAction.MarkUnresolved));
            }
            else if (selected?.Action == "ExcludeFinalFormat")
            {
                formatOverrides.Add(new(row.Format, FormatOverrideAction.ExcludeFinalFormat));
            }
            else if (selected?.BookId is long sourceId && original.ProposedSource?.BookId.Value != sourceId)
            {
                formatOverrides.Add(new(row.Format, FormatOverrideAction.SelectSource, new CalibreBookId(sourceId)));
            }
        }

        CalibreBookId? metadata = ReviewedMetadataSource?.BookId is long metadataId
            && generated.MetadataSource?.SelectedBookId.Value != metadataId
            ? new CalibreBookId(metadataId)
            : null;
        CalibreBookId[] retained = SelectedMetadataDuplicateGroup.Members
            .Where(value => value.IsRetainedSeparate)
            .Select(value => new CalibreBookId(value.BookId))
            .ToArray();
        UserRecommendationOverride proposed = new(
            generated.ModelVersion,
            generated.InputVersion,
            requestedStatus,
            _clock?.GetUtcNow() ?? DateTimeOffset.UtcNow,
            metadata,
            formatOverrides,
            retained);
        ApplyOverride(proposed);
    }

    private void ApplyOverride(UserRecommendationOverride proposed)
    {
        RecommendationOverrideOutcome outcome = ApplyRecommendationOverrideUseCase.Execute(
            SelectedMetadataDuplicateGroup!.Recommendation!,
            proposed);
        if (!outcome.IsSuccess)
        {
            ErrorMessage = string.Join(" ", outcome.Errors.Select(value => value.Message));
            ErrorAction = "Correct the selected records/formats or reset to the generated recommendation.";
            return;
        }

        ClearError();
        ReviewedConsolidationRecommendation reviewed = outcome.Reviewed!;
        SelectedMetadataDuplicateGroup.SetReviewed(reviewed);
        CleanupPlans?.UpdateContext(_currentSnapshot, reviewed);
        _recommendationReviews[new(_currentLibraryUuid!, SelectedMetadataDuplicateGroup.GroupId)] = reviewed;
        RefreshSelectedRecommendationBindings();
        ApplyMetadataDuplicateFilter();
    }

    private void ResetSelectedReview()
    {
        if (SelectedMetadataDuplicateGroup?.Recommendation is null || _currentLibraryUuid is null)
        {
            return;
        }

        ReviewedConsolidationRecommendation reviewed = ApplyRecommendationOverrideUseCase.Reset(SelectedMetadataDuplicateGroup.Recommendation);
        _recommendationReviews.Remove(new(_currentLibraryUuid, SelectedMetadataDuplicateGroup.GroupId));
        SelectedMetadataDuplicateGroup.SetReviewed(reviewed);
        CleanupPlans?.UpdateContext(_currentSnapshot, reviewed);
        ClearError();
        RefreshSelectedRecommendationBindings();
        ApplyMetadataDuplicateFilter();
    }

    private async Task ExportRecommendationsAsync()
    {
        if (_currentSnapshot is null || _exportRecommendations is null || _exportFilePicker is null)
        {
            return;
        }

        string? destination = _exportFilePicker.PickJsonDestination();
        if (destination is null)
        {
            return;
        }

        ReviewedConsolidationRecommendation[] reviews = _allMetadataDuplicateGroups
            .Where(value => value.Reviewed is not null)
            .Select(value => value.Reviewed!)
            .ToArray();
        try
        {
            RecommendationExportWriteOutcome outcome = await _exportRecommendations.ExecuteAsync(
                _currentSnapshot,
                reviews,
                destination,
                CancellationToken.None).ConfigureAwait(true);
            if (outcome.IsSuccess)
            {
                StatusMessage = $"Recommendation review artifact exported to {outcome.PublishedPath}.";
                ClearError();
            }
            else
            {
                ErrorMessage = outcome.Error!.Message;
                ErrorAction = "Choose an existing destination outside the Calibre library and retry.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Recommendation export canceled; no partial artifact was published.";
        }
    }

    private void RefreshSelectedRecommendationBindings()
    {
        OnPropertyChanged(nameof(SelectedRecommendationFormats));
        OnPropertyChanged(nameof(SelectedRecommendationReasons));
        OnPropertyChanged(nameof(SelectedRecommendationWarnings));
        OnPropertyChanged(nameof(ReviewedMetadataSource));
        OnPropertyChanged(nameof(StaleOverrideSummary));
        OnPropertyChanged(nameof(MetadataDeferAction));
        NotifyRecommendationCommands();
    }

    private void NotifyRecommendationCommands()
    {
        AcceptRecommendationCommand.NotifyCanExecuteChanged();
        SaveManualAdjustmentCommand.NotifyCanExecuteChanged();
        KeepSeparateRecommendationCommand.NotifyCanExecuteChanged();
        MarkNotDuplicatesCommand.NotifyCanExecuteChanged();
        ResetRecommendationCommand.NotifyCanExecuteChanged();
        ExportRecommendationsCommand.NotifyCanExecuteChanged();
    }

    private void ApplyEpubFindingFilter()
    {
        IEnumerable<EpubAssessmentFindingRowViewModel> findings = SelectedEpubAssessment?.Findings ?? [];
        if (EpubFindingFilterMode != EpubFindingFilterMode.All)
        {
            findings = findings.Where(finding => string.Equals(finding.Severity, EpubFindingFilterMode.ToString(), StringComparison.Ordinal));
        }

        _epubFindings.ReplaceAll(findings);
    }

    private sealed record SnapshotPresentation(
        IReadOnlyList<BookRowViewModel> Books,
        IReadOnlyList<ExactDuplicateGroupRowViewModel> Groups,
        IReadOnlyList<MetadataDuplicateGroupRowViewModel> MetadataGroups,
        IReadOnlyList<EpubAssessmentRowViewModel> EpubAssessments,
        int MissingCount);

    private sealed record RecommendationReviewKey(
        string LibraryUuid,
        ExactMetadataDuplicateGroupId GroupId);

    private sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        public void ReplaceAll(IEnumerable<T> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            CheckReentrancy();
            Items.Clear();
            foreach (T value in values)
            {
                Items.Add(value);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    private sealed class CoalescingProgress : IProgress<LibraryScanProgress>
    {
        private readonly object _gate = new();
        private readonly Action<LibraryScanProgress> _handler;
        private readonly SynchronizationContext? _synchronizationContext = SynchronizationContext.Current;
        private LibraryScanProgress? _latest;
        private bool _isActive = true;
        private bool _isScheduled;

        public CoalescingProgress(Action<LibraryScanProgress> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _handler = handler;
        }

        public void Report(LibraryScanProgress value)
        {
            lock (_gate)
            {
                if (!_isActive)
                {
                    return;
                }

                _latest = value;
                if (_isScheduled)
                {
                    return;
                }

                _isScheduled = true;
            }

            PostDrain();
        }

        public void Complete()
        {
            lock (_gate)
            {
                _isActive = false;
                _latest = null;
            }
        }

        private void PostDrain()
        {
            if (_synchronizationContext is null)
            {
                ThreadPool.QueueUserWorkItem(static state => ((CoalescingProgress)state!).Drain(), this);
            }
            else
            {
                _synchronizationContext.Post(static state => ((CoalescingProgress)state!).Drain(), this);
            }
        }

        private void Drain()
        {
            LibraryScanProgress? value;
            lock (_gate)
            {
                value = _isActive ? _latest : null;
                _latest = null;
                _isScheduled = false;
            }

            if (value is not null)
            {
                _handler(value);
            }

            lock (_gate)
            {
                if (!_isActive || _latest is null || _isScheduled)
                {
                    return;
                }

                _isScheduled = true;
            }

            PostDrain();
        }
    }
}
