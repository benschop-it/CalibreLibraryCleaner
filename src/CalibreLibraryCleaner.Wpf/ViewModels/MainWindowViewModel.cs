using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
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
    private readonly IEbookViewerLauncher? _ebookViewer;
    private readonly PersistedLibrarySnapshotsUseCase? _persistedSnapshots;
    private readonly ILibraryStateSession? _libraryStateSession;
    private readonly SynchronizationContext? _uiContext;
    private readonly BulkObservableCollection<BookRowViewModel> _books = [];
    private readonly BulkObservableCollection<string> _persistedLibraryPaths = [];
    private readonly BulkObservableCollection<ExactDuplicateGroupRowViewModel> _exactDuplicateGroups = [];
    private readonly BulkObservableCollection<MetadataDuplicateGroupRowViewModel> _metadataDuplicateGroups = [];
    private readonly BulkObservableCollection<ExpandedCandidateGroupRowViewModel> _expandedCandidateGroups = [];
    private readonly BulkObservableCollection<EpubAssessmentRowViewModel> _epubAssessments = [];
    private readonly BulkObservableCollection<EpubAssessmentFindingRowViewModel> _epubFindings = [];
    private readonly BulkObservableCollection<PdfAssessmentRowViewModel> _pdfAssessments = [];
    private readonly BulkObservableCollection<PdfAssessmentFindingRowViewModel> _pdfFindings = [];
    private readonly Dictionary<RecommendationReviewKey, ReviewedConsolidationRecommendation> _recommendationReviews = [];
    private IReadOnlyList<MetadataDuplicateGroupRowViewModel> _allMetadataDuplicateGroups = [];
    private CancellationTokenSource? _scanCancellation;
    private string _selectedLibraryPath = string.Empty;
    private string? _selectedPersistedLibraryPath;
    private string _statusMessage = "Choose a Calibre library folder.";
    private string _errorMessage = string.Empty;
    private string _errorAction = string.Empty;
    private string _exactDuplicateSummary = "No exact file duplicate groups have been found.";
    private string _metadataDuplicateSummary = "No exact metadata candidate groups have been found.";
    private string _expandedCandidateSummary = "Run a fresh scan to discover expanded candidates.";
    private string _metadataDuplicateFilterText = string.Empty;
    private MetadataDuplicateFilterMode _metadataDuplicateFilterMode;
    private bool _isBusy;
    private double _progressPercentage;
    private bool _isProgressIndeterminate;
    private BookRowViewModel? _selectedBook;
    private ExactDuplicateGroupRowViewModel? _selectedExactDuplicateGroup;
    private ExactDuplicateMemberRowViewModel? _selectedExactDuplicateMember;
    private MetadataDuplicateGroupRowViewModel? _selectedMetadataDuplicateGroup;
    private MetadataDuplicateMemberRowViewModel? _selectedMetadataDuplicateMember;
    private ExpandedCandidateGroupRowViewModel? _selectedExpandedCandidateGroup;
    private ExpandedCandidateMemberRowViewModel? _selectedExpandedCandidateMember;
    private EpubAssessmentRowViewModel? _selectedEpubAssessment;
    private EpubFindingFilterMode _epubFindingFilterMode;
    private PdfAssessmentRowViewModel? _selectedPdfAssessment;
    private EpubFindingFilterMode _pdfFindingFilterMode;
    private string? _currentLibraryUuid;
    private LibrarySnapshot? _currentSnapshot;
    private bool _isCurrentSnapshotFresh;

    public MainWindowViewModel(
        ValidateLibraryUseCase validateLibrary,
        ScanLibraryUseCase scanLibrary,
        ILibraryFolderPicker folderPicker,
        ExportRecommendationsUseCase? exportRecommendations = null,
        IRecommendationExportFilePicker? exportFilePicker = null,
        IClock? clock = null,
        IEbookViewerLauncher? ebookViewer = null,
        ExactBinaryCleanupPlanWorkspaceViewModel? exactBinaryCleanupPlans = null,
        MetadataCandidateCleanupWorkspaceViewModel? metadataCandidateCleanup = null,
        PersistedLibrarySnapshotsUseCase? persistedSnapshots = null,
        ILibraryStateSession? libraryStateSession = null)
    {
        _validateLibrary = validateLibrary;
        _scanLibrary = scanLibrary;
        _folderPicker = folderPicker;
        _exportRecommendations = exportRecommendations;
        _exportFilePicker = exportFilePicker;
        _clock = clock;
        _ebookViewer = ebookViewer;
        _persistedSnapshots = persistedSnapshots;
        _libraryStateSession = libraryStateSession;
        _uiContext = SynchronizationContext.Current;
        if (_libraryStateSession is not null)
            _libraryStateSession.StateChanged += OnLibraryStateChanged;
        ExactBinaryCleanupPlans = exactBinaryCleanupPlans;
        MetadataCandidateCleanup = metadataCandidateCleanup;
        Books = new ReadOnlyObservableCollection<BookRowViewModel>(_books);
        PersistedLibraryPaths = new ReadOnlyObservableCollection<string>(_persistedLibraryPaths);
        ExactDuplicateGroups = new ReadOnlyObservableCollection<ExactDuplicateGroupRowViewModel>(_exactDuplicateGroups);
        MetadataDuplicateGroups = new ReadOnlyObservableCollection<MetadataDuplicateGroupRowViewModel>(
            _metadataDuplicateGroups);
        ExpandedCandidateGroups = new ReadOnlyObservableCollection<ExpandedCandidateGroupRowViewModel>(
            _expandedCandidateGroups);
        EpubAssessments = new ReadOnlyObservableCollection<EpubAssessmentRowViewModel>(_epubAssessments);
        EpubFindings = new ReadOnlyObservableCollection<EpubAssessmentFindingRowViewModel>(_epubFindings);
        PdfAssessments = new ReadOnlyObservableCollection<PdfAssessmentRowViewModel>(_pdfAssessments);
        PdfFindings = new ReadOnlyObservableCollection<PdfAssessmentFindingRowViewModel>(_pdfFindings);
        SelectLibraryCommand = new AsyncRelayCommand(SelectLibraryAsync, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedLibraryPath));
        LoadPersistedSnapshotCommand = new AsyncRelayCommand(LoadPersistedSnapshotAsync, CanLoadPersistedSnapshot);
        CancelCommand = new RelayCommand(
            CancelScan,
            () => IsBusy && _scanCancellation is { IsCancellationRequested: false });
        OpenSelectedExactDuplicateCommand = new AsyncRelayCommand(
            OpenSelectedExactDuplicateAsync,
            () => !IsBusy && _ebookViewer is not null && SelectedExactDuplicateMember is not null);
        OpenSelectedMetadataCandidateCommand = new AsyncRelayCommand(
            OpenSelectedMetadataCandidateAsync,
            () => !IsBusy && _ebookViewer is not null && SelectedMetadataDuplicateMember is not null);
        OpenSelectedExpandedCandidateCommand = new AsyncRelayCommand(
            OpenSelectedExpandedCandidateAsync,
            () => !IsBusy && _ebookViewer is not null && SelectedExpandedCandidateMember is not null);
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
                LoadPersistedSnapshotCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? SelectedPersistedLibraryPath
    {
        get => _selectedPersistedLibraryPath;
        set
        {
            if (SetProperty(ref _selectedPersistedLibraryPath, value))
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    SelectedLibraryPath = value;
                }

                LoadPersistedSnapshotCommand.NotifyCanExecuteChanged();
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
                LoadPersistedSnapshotCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
                OpenSelectedExactDuplicateCommand.NotifyCanExecuteChanged();
                OpenSelectedMetadataCandidateCommand.NotifyCanExecuteChanged();
                OpenSelectedExpandedCandidateCommand.NotifyCanExecuteChanged();
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

    public ReadOnlyObservableCollection<string> PersistedLibraryPaths { get; }

    public ReadOnlyObservableCollection<ExactDuplicateGroupRowViewModel> ExactDuplicateGroups { get; }

    public ReadOnlyObservableCollection<MetadataDuplicateGroupRowViewModel> MetadataDuplicateGroups { get; }

    public ReadOnlyObservableCollection<ExpandedCandidateGroupRowViewModel> ExpandedCandidateGroups { get; }

    public ReadOnlyObservableCollection<EpubAssessmentRowViewModel> EpubAssessments { get; }

    public ReadOnlyObservableCollection<EpubAssessmentFindingRowViewModel> EpubFindings { get; }

    public ReadOnlyObservableCollection<PdfAssessmentRowViewModel> PdfAssessments { get; }

    public ReadOnlyObservableCollection<PdfAssessmentFindingRowViewModel> PdfFindings { get; }

    public ExactBinaryCleanupPlanWorkspaceViewModel? ExactBinaryCleanupPlans { get; }

    public MetadataCandidateCleanupWorkspaceViewModel? MetadataCandidateCleanup { get; }

    public IReadOnlyList<EpubFindingFilterMode> EpubFindingFilterModes { get; } = Enum.GetValues<EpubFindingFilterMode>();

    public IReadOnlyList<EpubFindingFilterMode> PdfFindingFilterModes { get; } = Enum.GetValues<EpubFindingFilterMode>();

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

    public string ExpandedCandidateSummary
    {
        get => _expandedCandidateSummary;
        private set => SetProperty(ref _expandedCandidateSummary, value);
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
                OnPropertyChanged(nameof(RetainedExactDuplicateMember));
                SelectedExactDuplicateMember = value?.RetainedMember
                    ?? (value is { Members.Count: > 0 } ? value.Members[0] : null);
            }
        }
    }

    public IReadOnlyList<ExactDuplicateMemberRowViewModel> SelectedExactDuplicateMembers =>
        SelectedExactDuplicateGroup?.Members ?? [];

    public ExactDuplicateMemberRowViewModel? SelectedExactDuplicateMember
    {
        get => _selectedExactDuplicateMember;
        set
        {
            if (!SetProperty(ref _selectedExactDuplicateMember, value)) return;
            OpenSelectedExactDuplicateCommand.NotifyCanExecuteChanged();
            if (value is null || SelectedExactDuplicateGroup is null) return;
            SelectedExactDuplicateGroup.RetainedMember = value;
            OnPropertyChanged(nameof(RetainedExactDuplicateMember));
        }
    }

    public ExactDuplicateMemberRowViewModel? RetainedExactDuplicateMember =>
        SelectedExactDuplicateGroup?.RetainedMember;

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
                SelectedMetadataDuplicateMember = value?.KeeperMember;
                ToggleMetadataDuplicateDeferredCommand.NotifyCanExecuteChanged();
                NotifyRecommendationCommands();
            }
        }
    }

    public IReadOnlyList<MetadataDuplicateMemberRowViewModel> SelectedMetadataDuplicateMembers =>
        SelectedMetadataDuplicateGroup?.Members ?? [];

    public MetadataDuplicateMemberRowViewModel? SelectedMetadataDuplicateMember
    {
        get => _selectedMetadataDuplicateMember;
        set
        {
            if (!SetProperty(ref _selectedMetadataDuplicateMember, value)) return;
            OpenSelectedMetadataCandidateCommand.NotifyCanExecuteChanged();
            if (value is null || SelectedMetadataDuplicateGroup is null) return;
            SelectedMetadataDuplicateGroup.KeeperMember = value;
        }
    }

    public ExpandedCandidateGroupRowViewModel? SelectedExpandedCandidateGroup
    {
        get => _selectedExpandedCandidateGroup;
        set
        {
            if (SetProperty(ref _selectedExpandedCandidateGroup, value))
            {
                OnPropertyChanged(nameof(SelectedExpandedCandidateMembers));
                SelectedExpandedCandidateMember = value is { Members.Count: > 0 } ? value.Members[0] : null;
            }
        }
    }

    public IReadOnlyList<ExpandedCandidateMemberRowViewModel> SelectedExpandedCandidateMembers =>
        SelectedExpandedCandidateGroup?.Members ?? [];

    public ExpandedCandidateMemberRowViewModel? SelectedExpandedCandidateMember
    {
        get => _selectedExpandedCandidateMember;
        set
        {
            if (SetProperty(ref _selectedExpandedCandidateMember, value))
                OpenSelectedExpandedCandidateCommand.NotifyCanExecuteChanged();
        }
    }

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
                OnPropertyChanged(nameof(EpubAssessmentStatusMessage));
            }
        }
    }

    public EpubFindingFilterMode EpubFindingFilterMode
    {
        get => _epubFindingFilterMode;
        set { if (SetProperty(ref _epubFindingFilterMode, value)) ApplyEpubFindingFilter(); }
    }

    public string SelectedEpubFeatureSummary => SelectedEpubAssessment?.FeatureSummary ?? "Select an EPUB assessment to view bounded format facts.";

    public string EpubAssessmentStatusMessage => SelectedEpubAssessment?.Status switch
    {
        "Unassessed" => "Not scored — unassessed. The file may still open in Calibre; this analyzer could not safely produce comparable quality facts.",
        "Disqualified" => "Not scored — disqualified because the file could not be opened or read.",
        _ when SelectedEpubAssessment?.Coverage == "FallbackReadable" => "Fallback-readable score. Safe local renderable content was found, but incomplete technical coverage limits the score to 70 and requires manual review for non-identical EPUB comparisons.",
        _ => string.Empty,
    };

    public PdfAssessmentRowViewModel? SelectedPdfAssessment
    {
        get => _selectedPdfAssessment;
        set
        {
            if (SetProperty(ref _selectedPdfAssessment, value))
            {
                ApplyPdfFindingFilter();
                OnPropertyChanged(nameof(SelectedPdfFeatureSummary));
                OnPropertyChanged(nameof(PdfDisqualificationMessage));
            }
        }
    }

    public EpubFindingFilterMode PdfFindingFilterMode
    {
        get => _pdfFindingFilterMode;
        set
        {
            if (SetProperty(ref _pdfFindingFilterMode, value))
            {
                ApplyPdfFindingFilter();
            }
        }
    }

    public string SelectedPdfFeatureSummary => SelectedPdfAssessment?.FeatureSummary
        ?? "Select a PDF assessment to view bounded format facts and classification limitations.";

    public string PdfDisqualificationMessage => SelectedPdfAssessment?.Status == "Disqualified"
        ? "Not scored — disqualified. See the disqualifying finding below."
        : string.Empty;

    public string MetadataDeferAction => SelectedMetadataDuplicateGroup?.IsDeferred == true
        ? "_Restore"
        : "_Defer";

    public IAsyncRelayCommand SelectLibraryCommand { get; }

    public IAsyncRelayCommand ScanCommand { get; }

    public IAsyncRelayCommand LoadPersistedSnapshotCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand OpenSelectedExactDuplicateCommand { get; }

    public IAsyncRelayCommand OpenSelectedMetadataCandidateCommand { get; }

    public IAsyncRelayCommand OpenSelectedExpandedCandidateCommand { get; }

    public IRelayCommand NextMetadataDuplicateGroupCommand { get; }

    public IRelayCommand PreviousMetadataDuplicateGroupCommand { get; }

    public IRelayCommand ToggleMetadataDuplicateDeferredCommand { get; }

    public IRelayCommand AcceptRecommendationCommand { get; }

    public IRelayCommand SaveManualAdjustmentCommand { get; }

    public IRelayCommand KeepSeparateRecommendationCommand { get; }

    public IRelayCommand MarkNotDuplicatesCommand { get; }

    public IRelayCommand ResetRecommendationCommand { get; }

    public IAsyncRelayCommand ExportRecommendationsCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await RefreshPersistedLibraryPathsAsync(cancellationToken).ConfigureAwait(true);
    }

    public void Dispose()
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        if (_libraryStateSession is not null)
            _libraryStateSession.StateChanged -= OnLibraryStateChanged;
    }

    private void OnLibraryStateChanged(object? sender, LibraryStateChangedEventArgs eventArgs)
    {
        if (_uiContext is null || !PathsEqual(SelectedLibraryPath, eventArgs.State.Snapshot.Identity.LibraryRoot))
            return;
        _uiContext.Post(_ => _ = ApplyProjectedStateAsync(eventArgs.State), null);
    }

    private async Task ApplyProjectedStateAsync(LibraryState state)
    {
        if (!PathsEqual(SelectedLibraryPath, state.Snapshot.Identity.LibraryRoot)) return;
        SnapshotPresentation presentation = await PreparePresentationAsync(
            state.Snapshot, CancellationToken.None).ConfigureAwait(true);
        LibraryState? latest = _libraryStateSession?.GetCurrent(state.Snapshot.Identity.LibraryRoot);
        if (latest?.GenerationId != state.GenerationId || latest.Revision != state.Revision) return;
        ApplySnapshot(state.Snapshot, presentation, state.IsAuthoritative);
        StatusMessage = state.IsAuthoritative
            ? $"Projected library state updated to revision {state.Revision.Value}. External Calibre changes require Rescan."
            : $"Library state is uncertain after revision {state.Revision.Value}. Rescan is required before cleanup or recovery.";
    }

    private async Task SelectLibraryAsync()
    {
        string? selected = _folderPicker.PickFolder(SelectedLibraryPath);
        if (selected is null)
        {
            return;
        }

        SelectedLibraryPath = selected;
        SelectedPersistedLibraryPath = _persistedLibraryPaths.FirstOrDefault(path => PathsEqual(path, selected));
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
                bool statePersisted = false;
                if (_libraryStateSession is not null)
                {
                    LibraryStateSessionOutcome stateStart = await _libraryStateSession
                        .StartFromScanAsync(outcome.Snapshot!, _scanCancellation.Token)
                        .ConfigureAwait(true);
                    statePersisted = stateStart.IsSuccess;
                    if (!stateStart.IsSuccess)
                    {
                        ErrorMessage = stateStart.Explanation ?? "The authoritative library state could not be persisted.";
                        ErrorAction = "Retry the scan before running cleanup or recovery.";
                    }
                }
                SnapshotPresentation presentation = await PreparePresentationAsync(
                    outcome.Snapshot!, _scanCancellation.Token).ConfigureAwait(true);
                ApplySnapshot(outcome.Snapshot!, presentation);
                if (_persistedSnapshots is not null && statePersisted)
                {
                    await RefreshPersistedLibraryPathsAsync(_scanCancellation.Token).ConfigureAwait(true);
                    SelectedPersistedLibraryPath = _persistedLibraryPaths
                        .FirstOrDefault(path => PathsEqual(path, outcome.Snapshot!.Identity.LibraryRoot));
                }
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

    private async Task LoadPersistedSnapshotAsync()
    {
        if (_persistedSnapshots is null || string.IsNullOrWhiteSpace(SelectedLibraryPath))
        {
            return;
        }

        IsBusy = true;
        ClearError();
        ProgressPercentage = 0;
        IsProgressIndeterminate = true;
        StatusMessage = "Loading saved library analysis...";
        await Task.Yield();
        try
        {
            if (_libraryStateSession is not null)
            {
                LibraryStateSessionOutcome stateLoad = await _libraryStateSession
                    .LoadAsync(SelectedLibraryPath, CancellationToken.None)
                    .ConfigureAwait(true);
                if (stateLoad.IsSuccess)
                {
                    LibraryState state = stateLoad.State!;
                    SnapshotPresentation projectedPresentation = await PreparePresentationAsync(
                        state.Snapshot, CancellationToken.None).ConfigureAwait(true);
                    SelectedLibraryPath = state.Snapshot.Identity.LibraryRoot;
                    ApplySnapshot(state.Snapshot, projectedPresentation, state.IsAuthoritative);
                    StatusMessage = state.IsAuthoritative
                        ? $"Loaded authoritative projected state revision {state.Revision.Value} from baseline {state.Snapshot.ScannedAt:u}. External Calibre changes require Rescan."
                        : $"Loaded uncertain projected state revision {state.Revision.Value}. Rescan is required before cleanup or recovery.";
                    await _persistedSnapshots.InvalidateAsync(
                        state.Snapshot.Identity.LibraryRoot, CancellationToken.None).ConfigureAwait(true);
                    await RefreshPersistedLibraryPathsAsync(CancellationToken.None).ConfigureAwait(true);
                    return;
                }
                if (stateLoad.ErrorCode != "LIBRARY_STATE.NOT_FOUND")
                {
                    ErrorMessage = stateLoad.Explanation ?? "The persisted projected state could not be loaded.";
                    ErrorAction = "Run an explicit scan to replace the projected state.";
                    StatusMessage = "Projected state loading failed. Current results were not replaced.";
                    return;
                }
            }
            PersistedLibrarySnapshotLoadResult load = await _persistedSnapshots
                .LoadAsync(SelectedLibraryPath, CancellationToken.None)
                .ConfigureAwait(true);
            if (!load.IsSuccess)
            {
                ErrorMessage = load.Error ?? "The persisted scan result could not be loaded.";
                ErrorAction = "Select another persisted library or run a fresh scan.";
                StatusMessage = "Persisted scan loading failed. Current results were not replaced.";
                return;
            }

            LibrarySnapshot snapshot = load.Snapshot!;
            LibraryState? migratedState = null;
            if (_libraryStateSession is not null)
            {
                LibraryStateSessionOutcome migration = await _libraryStateSession
                    .StartFromScanAsync(snapshot, CancellationToken.None)
                    .ConfigureAwait(true);
                if (!migration.IsSuccess)
                {
                    ErrorMessage = migration.Explanation
                        ?? "The persisted development snapshot could not be migrated to projected state.";
                    ErrorAction = "Retry Load or run an explicit scan.";
                    StatusMessage = "Persisted development snapshot migration failed. Current results were not replaced.";
                    return;
                }

                migratedState = migration.State!;
                PersistedLibrarySnapshotInvalidateResult invalidated = await _persistedSnapshots
                    .InvalidateAsync(snapshot.Identity.LibraryRoot, CancellationToken.None)
                    .ConfigureAwait(true);
                if (!invalidated.IsSuccess)
                {
                    ErrorMessage = invalidated.Error ?? "The migrated legacy snapshot could not be removed.";
                    ErrorAction = "The projected state is available; legacy cleanup will be retried later.";
                }
                await RefreshPersistedLibraryPathsAsync(CancellationToken.None).ConfigureAwait(true);
            }
            SnapshotPresentation presentation = await PreparePresentationAsync(
                snapshot, CancellationToken.None).ConfigureAwait(true);
            SelectedLibraryPath = snapshot.Identity.LibraryRoot;
            ApplySnapshot(snapshot, presentation, isFreshScan: migratedState?.IsAuthoritative == true);
            StatusMessage = migratedState is null
                ? $"Loaded persisted scan from {snapshot.ScannedAt:u}. Results may be stale; select Scan before cleanup or recovery work."
                : $"Migrated and loaded trusted development snapshot from {snapshot.ScannedAt:u} as authoritative projected state revision 0.";
        }
        finally
        {
            IsProgressIndeterminate = false;
            IsBusy = false;
        }
    }

    private void CancelScan()
    {
        _scanCancellation?.Cancel();
        CancelCommand.NotifyCanExecuteChanged();
        StatusMessage = "Scan canceled. Waiting for the current read to stop...";
    }

    private Task OpenSelectedExactDuplicateAsync()
    {
        ExactDuplicateMemberRowViewModel? member = SelectedExactDuplicateMember;
        return member is null
            ? Task.CompletedTask
            : OpenBookFormatAsync(member.ExpectedRelativePath, member.Format);
    }

    private Task OpenSelectedMetadataCandidateAsync()
    {
        MetadataDuplicateMemberRowViewModel? member = SelectedMetadataDuplicateMember;
        if (member is null) return Task.CompletedTask;
        if (member.LaunchRelativePath is null || member.LaunchFormat is null)
        {
            ErrorMessage = "The selected record has no present book format to open.";
            ErrorAction = "Choose another record or rescan after restoring its format files.";
            return Task.CompletedTask;
        }
        return OpenBookFormatAsync(member.LaunchRelativePath, member.LaunchFormat);
    }

    private Task OpenSelectedExpandedCandidateAsync()
    {
        ExpandedCandidateMemberRowViewModel? member = SelectedExpandedCandidateMember;
        if (member is null) return Task.CompletedTask;
        if (member.LaunchRelativePath is null || member.LaunchFormat is null)
        {
            ErrorMessage = "The selected expanded candidate has no present book format to open.";
            ErrorAction = "Choose another record or rescan after restoring its format files.";
            return Task.CompletedTask;
        }
        return OpenBookFormatAsync(member.LaunchRelativePath, member.LaunchFormat);
    }

    private async Task OpenBookFormatAsync(string expectedRelativePath, string format)
    {
        if (_ebookViewer is null || string.IsNullOrWhiteSpace(SelectedLibraryPath)) return;
        ClearError();
        try
        {
            EbookViewerLaunchResult result = await _ebookViewer.LaunchAsync(
                new(SelectedLibraryPath, expectedRelativePath), CancellationToken.None).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                StatusMessage = $"Opened selected {format} in Calibre ebook viewer.";
                return;
            }

            ErrorMessage = result.ErrorMessage ?? "The selected book could not be opened in Calibre ebook viewer.";
            ErrorAction = result.ErrorCode == "EBOOK_VIEWER_NOT_FOUND"
                ? "Install Calibre with ebook-viewer in the configured Calibre folder."
                : "Verify that the selected format still exists, then rescan if necessary.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Opening the selected book was canceled.";
        }
    }

    private void UpdateProgress(LibraryScanProgress progress)
    {
        StatusMessage = progress.Message;
        IsProgressIndeterminate = progress.TotalUnits is null;
        ProgressPercentage = progress.TotalUnits is > 0
            ? 100d * progress.CompletedUnits / progress.TotalUnits.Value
            : progress.Phase == LibraryScanPhase.Completed ? 100 : 0;
    }

    private async Task<SnapshotPresentation> PreparePresentationAsync(
        LibrarySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ProgressPercentage = 0;
        IsProgressIndeterminate = true;
        StatusMessage = $"Preparing {snapshot.Books.Count:N0} books and analysis results for display...";
        await Task.Yield();
        return await Task.Run(
            () => CreatePresentation(snapshot, cancellationToken),
            cancellationToken).ConfigureAwait(true);
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

        ExpandedCandidateGroupRowViewModel[] expandedGroups = new ExpandedCandidateGroupRowViewModel[
            snapshot.WorkLanguageCandidateGroups.Count];
        for (int index = 0; index < snapshot.WorkLanguageCandidateGroups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            expandedGroups[index] = new(snapshot.WorkLanguageCandidateGroups[index], booksById);
        }

        EpubAssessmentRowViewModel[] epubAssessments = new EpubAssessmentRowViewModel[snapshot.EpubAssessments.Count];
        for (int index = 0; index < snapshot.EpubAssessments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Domain.Assessments.EpubAssessment assessment = snapshot.EpubAssessments[index];
            booksById.TryGetValue(assessment.CalibreBookId, out CalibreBook? book);
            epubAssessments[index] = new(assessment, book);
        }

        PdfAssessmentRowViewModel[] pdfAssessments = new PdfAssessmentRowViewModel[snapshot.PdfAssessments.Count];
        for (int index = 0; index < snapshot.PdfAssessments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Domain.Assessments.PdfAssessment assessment = snapshot.PdfAssessments[index];
            booksById.TryGetValue(assessment.CalibreBookId, out CalibreBook? book);
            pdfAssessments[index] = new(assessment, book);
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

        return new(books, groups, metadataGroups, expandedGroups, epubAssessments, pdfAssessments, missingCount);
    }

    private void ApplySnapshot(
        LibrarySnapshot snapshot,
        SnapshotPresentation presentation,
        bool isFreshScan = true)
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
        _isCurrentSnapshotFresh = isFreshScan;
        if (isFreshScan)
        {
            ExactBinaryCleanupPlans?.UpdateContext(snapshot, _exactDuplicateGroups);
        }
        else
        {
            ExactBinaryCleanupPlans?.UpdateContext(null, _exactDuplicateGroups);
        }
        _allMetadataDuplicateGroups = presentation.MetadataGroups;
        ApplyMetadataDuplicateFilter();
        MetadataCandidateCleanup?.UpdateContext(isFreshScan ? snapshot : null, _allMetadataDuplicateGroups);
        _expandedCandidateGroups.ReplaceAll(presentation.ExpandedGroups);
        SelectedExpandedCandidateGroup = _expandedCandidateGroups.FirstOrDefault();
        ExpandedCandidateSummary = snapshot.MatchingRunSummary.Status switch
        {
            MatchingEvidenceStatus.Unavailable =>
                "Expanded matching evidence is unavailable for this snapshot. Run a fresh scan to generate it.",
            MatchingEvidenceStatus.Stale =>
                "Expanded matching evidence is stale after library changes. Run a fresh scan to regenerate it.",
            _ when presentation.ExpandedGroups.Count == 0 =>
                $"No expanded work-language candidate groups were found from {snapshot.MatchingRunSummary.RetainedPairCount:N0} retained pairs.",
            _ =>
                $"{presentation.ExpandedGroups.Count:N0} expanded review-only groups from {snapshot.MatchingRunSummary.RetainedPairCount:N0} retained pairs; {snapshot.MatchingRunSummary.ContentSignaturesRequested:N0} content signatures requested.",
        };
        _epubAssessments.ReplaceAll(presentation.EpubAssessments);
        SelectedEpubAssessment = _epubAssessments.FirstOrDefault();
        _pdfAssessments.ReplaceAll(presentation.PdfAssessments);
        SelectedPdfAssessment = _pdfAssessments.FirstOrDefault();
        StatusMessage = snapshot.Books.Count == 0
            ? "Scan complete. The library contains no books."
            : $"Scan complete: {snapshot.Books.Count} books, {snapshot.ExactBinaryDuplicateGroups.Count} exact file duplicate groups, {snapshot.ExactMetadataDuplicateGroups.Count} exact metadata candidate groups, {snapshot.WorkLanguageCandidateGroups.Count} expanded review-only groups, {presentation.MissingCount} missing format files.";
        IsProgressIndeterminate = false;
        ProgressPercentage = 100;
        ExportRecommendationsCommand.NotifyCanExecuteChanged();
    }

    private bool CanLoadPersistedSnapshot() => !IsBusy
        && _persistedSnapshots is not null
        && !string.IsNullOrWhiteSpace(SelectedLibraryPath)
        && _persistedLibraryPaths.Any(path => PathsEqual(path, SelectedLibraryPath));

    private async Task RefreshPersistedLibraryPathsAsync(CancellationToken cancellationToken)
    {
        if (_persistedSnapshots is null)
        {
            return;
        }

        PersistedLibrarySnapshotListResult result = await _persistedSnapshots
            .ListAsync(cancellationToken)
            .ConfigureAwait(true);
        _persistedLibraryPaths.ReplaceAll(result.Snapshots.Select(snapshot => snapshot.LibraryRoot));
        if (SelectedPersistedLibraryPath is not null
            && !_persistedLibraryPaths.Any(path => PathsEqual(path, SelectedPersistedLibraryPath)))
        {
            SelectedPersistedLibraryPath = null;
        }

        if (result.Error is not null)
        {
            ErrorMessage = result.Error;
            ErrorAction = "Fresh library scans remain available.";
        }

        LoadPersistedSnapshotCommand.NotifyCanExecuteChanged();
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        first.TrimEnd('\\', '/'),
        second.TrimEnd('\\', '/'),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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

    private void ApplyPdfFindingFilter()
    {
        IEnumerable<PdfAssessmentFindingRowViewModel> findings = SelectedPdfAssessment?.Findings ?? [];
        if (PdfFindingFilterMode != EpubFindingFilterMode.All)
        {
            findings = findings.Where(finding => string.Equals(finding.Severity, PdfFindingFilterMode.ToString(), StringComparison.Ordinal));
        }

        _pdfFindings.ReplaceAll(findings);
    }

    private sealed record SnapshotPresentation(
        IReadOnlyList<BookRowViewModel> Books,
        IReadOnlyList<ExactDuplicateGroupRowViewModel> Groups,
        IReadOnlyList<MetadataDuplicateGroupRowViewModel> MetadataGroups,
        IReadOnlyList<ExpandedCandidateGroupRowViewModel> ExpandedGroups,
        IReadOnlyList<EpubAssessmentRowViewModel> EpubAssessments,
        IReadOnlyList<PdfAssessmentRowViewModel> PdfAssessments,
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
