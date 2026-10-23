using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Wpf.Services;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly IQuarantineDestinationInspector? _quarantineDestinationInspector;
    private readonly IQuarantineCleanupExecutor? _quarantineCleanupExecutor;
    private readonly IQuarantineCleanupConfirmationService? _quarantineCleanupConfirmation;
    private readonly BulkObservableCollection<QuarantineReviewSubjectRowViewModel> _quarantineReviewSubjects = [];
    private QuarantineReviewSubjectRowViewModel[] _allQuarantineReviewSubjects = [];
    private QuarantineReviewSubjectRowViewModel[] _selectedQuarantineReviewSubjects = [];
    private QuarantineReviewWorkspace? _quarantineReviewWorkspace;
    private QuarantineReviewSubjectRowViewModel? _selectedQuarantineReviewSubject;
    private QuarantineReviewFilterMode _quarantineReviewFilterMode = QuarantineReviewFilterMode.Proposed;
    private string _quarantineReviewSummary = "Complete final author and managed-name cleanup, then prepare quarantine review.";
    private string _quarantineDestinationPath = string.Empty;
    private string _quarantineManualRecordId = string.Empty;
    private bool _finalAuthorStageCompleted;
    private bool _quarantineCompleted;

    public ReadOnlyObservableCollection<QuarantineReviewSubjectRowViewModel> QuarantineReviewSubjects { get; }

    public IReadOnlyList<QuarantineReviewFilterMode> QuarantineReviewFilterModes { get; } =
        Enum.GetValues<QuarantineReviewFilterMode>();

    public QuarantineReviewFilterMode QuarantineReviewFilterMode
    {
        get => _quarantineReviewFilterMode;
        set
        {
            if (SetProperty(ref _quarantineReviewFilterMode, value)) ApplyQuarantineReviewFilter();
        }
    }

    public QuarantineReviewSubjectRowViewModel? SelectedQuarantineReviewSubject
    {
        get => _selectedQuarantineReviewSubject;
        set => SetProperty(ref _selectedQuarantineReviewSubject, value);
    }

    public string QuarantineReviewSummary
    {
        get => _quarantineReviewSummary;
        private set => SetProperty(ref _quarantineReviewSummary, value);
    }

    public string QuarantineDestinationPath
    {
        get => _quarantineDestinationPath;
        private set => SetProperty(ref _quarantineDestinationPath, value);
    }

    public string QuarantineManualRecordId
    {
        get => _quarantineManualRecordId;
        set
        {
            if (SetProperty(ref _quarantineManualRecordId, value ?? string.Empty))
                AddManualQuarantineRecordCommand.NotifyCanExecuteChanged();
        }
    }

    public IRelayCommand ClearAllQuarantineIncludeCommand { get; }
    public IRelayCommand CheckAllQuarantineIncludeCommand { get; }
    public IRelayCommand CheckSelectedQuarantineIncludeCommand { get; }
    public IRelayCommand UncheckSelectedQuarantineIncludeCommand { get; }
    public IRelayCommand AddManualQuarantineRecordCommand { get; }

    public void UpdateQuarantineReviewSelection(
        IEnumerable<QuarantineReviewSubjectRowViewModel> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        _selectedQuarantineReviewSubjects = subjects
            .Where(_allQuarantineReviewSubjects.Contains)
            .Distinct()
            .ToArray();
        NotifyQuarantineCommands();
    }

    private async Task PrepareQuarantineReviewAsync(LibraryState state)
    {
        if (_finalAuthorReviewWorkspace is null || _quarantineDestinationInspector is null) return;
        QuarantineReviewWorkspace workspace = PrepareQuarantineReviewUseCase.Execute(
            state,
            _finalAuthorReviewWorkspace);
        QuarantineDestinationInspection inspection = await _quarantineDestinationInspector.InspectAsync(
            workspace.LibraryRoot,
            workspace.DestinationRoot,
            _scanCancellation?.Token ?? CancellationToken.None).ConfigureAwait(true);
        if (!inspection.IsSuccess)
        {
            ErrorMessage = $"Quarantine destination preflight failed ({inspection.FailureCode}).";
            ErrorAction = "Verify the fixed sibling destination is a physical compatible Calibre library, then retry.";
            return;
        }
        workspace = PrepareQuarantineReviewUseCase.BindDestination(workspace, inspection.Identity!);
        ApplyQuarantineReviewWorkspace(workspace);
        IsProgressIndeterminate = false;
        ProgressPercentage = 100;
        StatusMessage = $"Unresolvable review ready for {workspace.Subjects.Count:N0} record(s); "
            + $"{workspace.Subjects.Count(value => value.Include):N0} checked.";
    }

    private async Task ExecuteQuarantineAsync(LibraryState state)
    {
        if (_quarantineReviewWorkspace is null
            || _quarantineCleanupExecutor is null
            || _quarantineCleanupConfirmation is null)
            return;
        int selected = _quarantineReviewWorkspace.Subjects.Count(value => value.Include);
        if (selected == 0)
        {
            StatusMessage = "No quarantine records are checked.";
            return;
        }
        QuarantineDestinationIdentity destination = _quarantineReviewWorkspace.DestinationIdentity!;
        if (!_quarantineCleanupConfirmation.ConfirmMove(
                selected,
                _quarantineReviewWorkspace.DestinationRoot,
                destination.Exists))
        {
            StatusMessage = "Quarantine move canceled. Confirm complete external backups before retrying.";
            return;
        }
        IsProgressIndeterminate = false;
        Progress<QuarantineCleanupProgress> progress = new(value =>
            UpdateOperationProgress(value.Message, value.CompletedOperations, value.TotalOperations));
        QuarantineCleanupResult result = await _quarantineCleanupExecutor.ExecuteAsync(new(
            state.Snapshot.Identity.LibraryRoot,
            state.GenerationId,
            state.Revision,
            _quarantineReviewWorkspace,
            ExternalBackupsConfirmed: true), progress, _scanCancellation?.Token ?? CancellationToken.None)
            .ConfigureAwait(true);
        CandidateCleanupResultSummary = $"Copied {result.CopiedRecordCount:N0} record(s); "
            + $"removed {result.RemovedSourceRecordCount:N0} verified source record(s).";
        LibraryState? final = _libraryStateSession?.GetCurrent(SelectedLibraryPath);
        if (final is not null)
        {
            SnapshotPresentation presentation = await PreparePresentationAsync(
                final.Snapshot, CancellationToken.None).ConfigureAwait(true);
            ApplySnapshot(final.Snapshot, presentation, final.IsAuthoritative);
        }
        if (result.IsCompleted)
        {
            _quarantineCompleted = true;
            _quarantineReviewWorkspace = null;
            _allQuarantineReviewSubjects = [];
            _quarantineReviewSubjects.ReplaceAll([]);
            UpdateQuarantineReviewSelection([]);
            SelectedQuarantineReviewSubject = null;
            QuarantineReviewSummary = "Unresolvable record quarantine completed. Rescan before another mutation.";
            StatusMessage = "Unresolvable record quarantine completed.";
        }
        else
        {
            StatusMessage = string.Join(" ", result.Issues.Select(value =>
                $"{value.Code}: {value.Explanation}"));
        }
        NotifyCandidateCleanupStateChanged();
    }

    private void ApplyQuarantineReviewWorkspace(QuarantineReviewWorkspace workspace)
    {
        _quarantineReviewWorkspace = workspace;
        _allQuarantineReviewSubjects = workspace.Subjects.Select(value =>
            new QuarantineReviewSubjectRowViewModel(value, PersistQuarantineIncludeAsync)).ToArray();
        QuarantineDestinationPath = workspace.DestinationRoot;
        UpdateQuarantineReviewSelection([]);
        ApplyQuarantineReviewFilter();
        NotifyCandidateCleanupStateChanged();
    }

    private Task PersistQuarantineIncludeAsync(CalibreBookId bookId, bool include)
    {
        if (_quarantineReviewWorkspace is null) return Task.CompletedTask;
        using IDisposable? operation = _operationCoordinator.TryBegin();
        if (operation is null)
        {
            RefreshQuarantineReviewRow(bookId);
            return Task.CompletedTask;
        }
        _quarantineReviewWorkspace = PrepareQuarantineReviewUseCase.SetInclude(
            _quarantineReviewWorkspace,
            [bookId],
            include);
        RefreshQuarantineReviewRow(bookId);
        ApplyQuarantineReviewFilter();
        return Task.CompletedTask;
    }

    private void SetAllQuarantineInclude(bool include)
    {
        if (_quarantineReviewWorkspace is null) return;
        _quarantineReviewWorkspace = PrepareQuarantineReviewUseCase.SetAllInclude(
            _quarantineReviewWorkspace,
            include);
        ApplyQuarantineReviewWorkspace(_quarantineReviewWorkspace);
        StatusMessage = include
            ? "Checked all quarantine review records."
            : "Cleared all quarantine Include choices.";
    }

    private bool CanSetSelectedQuarantineInclude(bool include) => !IsBusy
        && !_operationCoordinator.IsOperationActive
        && _selectedQuarantineReviewSubjects.Any(value => value.Include != include);

    private void SetSelectedQuarantineInclude(bool include)
    {
        if (_quarantineReviewWorkspace is null) return;
        CalibreBookId[] ids = _selectedQuarantineReviewSubjects
            .Where(value => value.Include != include)
            .Select(value => value.BookId)
            .ToArray();
        if (ids.Length == 0) return;
        _quarantineReviewWorkspace = PrepareQuarantineReviewUseCase.SetInclude(
            _quarantineReviewWorkspace,
            ids,
            include);
        ApplyQuarantineReviewWorkspace(_quarantineReviewWorkspace);
        StatusMessage = $"{(include ? "Checked" : "Unchecked")} {ids.Length:N0} selected quarantine record(s).";
    }

    private void AddManualQuarantineRecord()
    {
        if (_quarantineReviewWorkspace is null
            || _libraryStateSession?.GetCurrent(SelectedLibraryPath) is not { } state
            || !long.TryParse(QuarantineManualRecordId, out long value)
            || value <= 0)
            return;
        try
        {
            _quarantineReviewWorkspace = PrepareQuarantineReviewUseCase.IncludeManually(
                state,
                _quarantineReviewWorkspace,
                new(value));
            QuarantineManualRecordId = string.Empty;
            ApplyQuarantineReviewWorkspace(_quarantineReviewWorkspace);
            StatusMessage = $"Included record {value:N0} manually in quarantine review.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            ErrorMessage = exception.Message;
        }
    }

    private void RefreshQuarantineReviewRow(CalibreBookId bookId)
    {
        if (_quarantineReviewWorkspace is null) return;
        QuarantineReviewSubject subject = _quarantineReviewWorkspace.Subjects.Single(value => value.BookId == bookId);
        _allQuarantineReviewSubjects.First(value => value.BookId == bookId).Update(subject);
    }

    private void ApplyQuarantineReviewFilter()
    {
        CalibreBookId? selectedId = SelectedQuarantineReviewSubject?.BookId;
        QuarantineReviewSubjectRowViewModel[] visible = _allQuarantineReviewSubjects
            .Where(value => value.MatchesFilter(QuarantineReviewFilterMode))
            .ToArray();
        _quarantineReviewSubjects.ReplaceAll(visible);
        UpdateQuarantineReviewSelection([]);
        SelectedQuarantineReviewSubject = selectedId is null
            ? visible.FirstOrDefault()
            : visible.FirstOrDefault(value => value.BookId == selectedId.Value) ?? visible.FirstOrDefault();
        int checkedCount = _allQuarantineReviewSubjects.Count(value => value.Include);
        int manualCount = _allQuarantineReviewSubjects.Count(value => value.IsManual);
        string destinationState = _quarantineReviewWorkspace?.DestinationIdentity?.Exists == true
            ? "existing destination"
            : "new destination";
        QuarantineReviewSummary = _allQuarantineReviewSubjects.Length == 0
            ? $"No unresolvable records were proposed; {destinationState}."
            : $"{visible.Length:N0} of {_allQuarantineReviewSubjects.Length:N0} records visible; "
                + $"checked {checkedCount:N0}, manually included {manualCount:N0}; {destinationState}.";
        NotifyQuarantineCommands();
        NotifyCandidateCleanupStateChanged();
    }

    private void ClearQuarantineReview()
    {
        _quarantineReviewWorkspace = null;
        _allQuarantineReviewSubjects = [];
        _quarantineReviewSubjects.ReplaceAll([]);
        UpdateQuarantineReviewSelection([]);
        SelectedQuarantineReviewSubject = null;
        QuarantineDestinationPath = string.Empty;
        QuarantineManualRecordId = string.Empty;
        QuarantineReviewSummary = "Complete final author and managed-name cleanup, then prepare quarantine review.";
        _quarantineCompleted = false;
        NotifyCandidateCleanupStateChanged();
    }

    private void NotifyQuarantineCommands()
    {
        ClearAllQuarantineIncludeCommand.NotifyCanExecuteChanged();
        CheckAllQuarantineIncludeCommand.NotifyCanExecuteChanged();
        CheckSelectedQuarantineIncludeCommand.NotifyCanExecuteChanged();
        UncheckSelectedQuarantineIncludeCommand.NotifyCanExecuteChanged();
        AddManualQuarantineRecordCommand.NotifyCanExecuteChanged();
    }
}
