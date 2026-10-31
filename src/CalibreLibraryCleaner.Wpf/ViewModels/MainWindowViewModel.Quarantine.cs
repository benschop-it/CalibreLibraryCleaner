using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
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
    private bool _showAllQuarantineRecords;
    private string _quarantineReviewSummary = "Complete final author and managed-name cleanup, then prepare quarantine review.";
    private string _quarantineDestinationPath = string.Empty;
    private string _quarantineManualRecordId = string.Empty;
    private string _quarantineVerificationSummary = string.Empty;
    private bool _finalAuthorStageCompleted;
    private bool _quarantineCompleted;
    private bool _identityRepairReviewReady;
    private EditionMetadataProvidersBatchResult? _identityRepairProviderResults;
    private HashSet<CalibreBookId> _verifiedManagedNameBookIds = [];

    public ReadOnlyObservableCollection<QuarantineReviewSubjectRowViewModel> QuarantineReviewSubjects { get; }

    public bool ShowAllQuarantineRecords
    {
        get => _showAllQuarantineRecords;
        set
        {
            if (SetProperty(ref _showAllQuarantineRecords, value)) ApplyQuarantineReviewFilter();
        }
    }

    public string QuarantineVerificationSummary
    {
        get => _quarantineVerificationSummary;
        private set => SetProperty(ref _quarantineVerificationSummary, value);
    }

    public QuarantineReviewSubjectRowViewModel? SelectedQuarantineReviewSubject
    {
        get => _selectedQuarantineReviewSubject;
        set
        {
            if (SetProperty(ref _selectedQuarantineReviewSubject, value))
                ReviewIdentityRepairsCommand.NotifyCanExecuteChanged();
        }
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
    public IRelayCommand AcceptSelectedQualificationCommand { get; }
    public IAsyncRelayCommand ReviewIdentityRepairsCommand { get; }
    public IAsyncRelayCommand ApplyCheckedIdentityMetadataCommand { get; }
    public IAsyncRelayCommand MoveCheckedQuarantineCommand { get; }
    public bool HasIdentityRepairReview => _identityRepairReviewReady;
    public string IdentityRepairActionText =>
        $"Apply {_metadataReviewWorkspace?.Subjects.Count(value => value.Apply) ?? 0:N0} checked metadata repair(s)";
    public string QuarantineMoveActionText =>
        $"Move {_quarantineReviewWorkspace?.Subjects.Count(value => value.Include) ?? 0:N0} checked book(s) to quarantine";

    private bool CanApplyCheckedIdentityMetadata() => !IsBusy
        && !_operationCoordinator.IsOperationActive && !_quarantineCompleted
        && _identityRepairReviewReady
        && _executeUnifiedCandidateCleanup is not null && _metadataMutationConfirmation is not null
        && _metadataReviewWorkspace is { } workspace && workspace.Subjects.Any(value => value.Apply)
        && _libraryStateSession?.GetCurrent(SelectedLibraryPath) is
        { IsAuthoritative: true, IsWorkflowCheckpointCurrent: true } state
        && state.WorkflowCheckpoint.Phase == LibraryWorkflowPhase.Completed
        && ReferenceEquals(state.Snapshot, _currentSnapshot)
        && workspace.GenerationId == state.GenerationId && workspace.Revision == state.Revision;

    private bool CanMoveCheckedQuarantine() => _finalAuthorStageCompleted && !_quarantineCompleted
        && _quarantineReviewWorkspace?.Subjects.Any(value => value.Include) == true
        && CanRunCandidateCleanup();

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
        StatusMessage = "Preparing quarantine choices from current catalog metadata and existing evidence; "
            + "no library-wide document inspection or online lookup.";
        QuarantineReviewWorkspace workspace = PrepareQuarantineReviewUseCase.Execute(
            state,
            _finalAuthorReviewWorkspace,
            managedLayoutVerified: true,
            _identityRepairProviderResults,
            _verifiedManagedNameBookIds);
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
        StatusMessage = "Problem review ready. Only detected problems and checked moves are shown by default. "
            + "No files are moved until you confirm Move checked records to quarantine.";
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
            StatusMessage = _quarantineReviewWorkspace.IsHighQualityComplete
                ? "High-quality library complete: all retained records are qualified or verified moved."
                : "No books are checked for quarantine; nothing will be moved. "
                    + $"{_quarantineReviewWorkspace.PendingCount:N0} book(s) are not fully verified, "
                    + "which does not mean their metadata is wrong. Optional verification tools explain the remaining checks.";
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
        CandidateCleanupResultSummary = $"Verified completed copies: {result.CopiedRecordCount:N0} record(s); "
            + $"removed {result.RemovedSourceRecordCount:N0} verified source record(s).";
        if (!result.IsCompleted)
            CandidateCleanupResultSummary += " The stopped chunk may have copied additional destination records; "
                + "they are not counted as committed successes.";
        LibraryState? final = _libraryStateSession?.GetCurrent(SelectedLibraryPath);
        if (final is not null)
        {
            SnapshotPresentation presentation = await PreparePresentationAsync(
                final.Snapshot, CancellationToken.None).ConfigureAwait(true);
            ApplySnapshot(final.Snapshot, presentation, final.IsAuthoritative);
        }
        if (result.IsCompleted)
        {
            CalibreBookId[] moved = _quarantineReviewWorkspace.Subjects
                .Where(value => value.Include).Select(value => value.BookId).ToArray();
            if (result.RemovedSourceRecordCount != moved.Length || result.CopiedRecordCount != moved.Length)
                throw new InvalidOperationException("Completed quarantine returned inconsistent record accounting.");
            QuarantineReviewWorkspace accounting = PrepareQuarantineReviewUseCase.RecordVerifiedMoves(
                _quarantineReviewWorkspace, moved);
            _quarantineCompleted = true;
            _identityRepairReviewReady = false;
            ClearMetadataReview();
            ApplyQuarantineReviewWorkspace(accounting);
            StatusMessage = accounting.IsHighQualityComplete
                ? "High-quality library complete: retained records equal qualified records remaining plus verified moves."
                : $"Quarantine move completed: {moved.Length:N0} checked book(s) moved; unchecked books remain in the source. "
                    + "Other books may still lack optional verification, which is not evidence that they are bad. "
                    + "Run an explicit Scan before another mutation.";
        }
        else
        {
            StatusMessage = string.Join(" ", result.Issues.Select(value =>
                $"{value.Code}: {value.Explanation}"));
        }
        NotifyCandidateCleanupStateChanged();
    }

    private void AcceptSelectedQualification()
    {
        if (_quarantineReviewWorkspace is null
            || _libraryStateSession?.GetCurrent(SelectedLibraryPath) is not { } state) return;
        using IDisposable? operation = _operationCoordinator.TryBegin();
        if (operation is null) return;
        try
        {
            _quarantineReviewWorkspace = PrepareQuarantineReviewUseCase.AcceptReviewed(
                state, _quarantineReviewWorkspace,
                _selectedQuarantineReviewSubjects.Select(value => value.BookId).ToArray());
            ApplyQuarantineReviewWorkspace(_quarantineReviewWorkspace);
            StatusMessage = "Selected title, complete author credits, content and disclosed format defects accepted by reviewer.";
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
    }

    private async Task ReviewIdentityRepairsAsync()
    {
        if (_prepareMetadataReview is null || _finalAuthorReviewWorkspace is null
            || _libraryStateSession?.GetCurrent(SelectedLibraryPath) is not { } state) return;
        CalibreBookId[] selectedIds = SelectedRepairBookIds();
        if (selectedIds.Length == 0) return;
        using IDisposable? operation = _operationCoordinator.TryBegin();
        if (operation is null) return;
        IsBusy = true;
        ClearError();
        StartCandidateHeartbeat();
        try
        {
            string scope = selectedIds.Length == 1
                ? $"record ID {selectedIds[0].Value}" : $"{selectedIds.Length:N0} selected records";
            string message = $"Finding repairs for {scope} only";
            UpdateCandidateProgress(new(
                CandidatePreparationPhase.ResolvingIdentityRepairs, 0, 0, CandidateProgressUnit.Records,
                message));
            if (_inspectFrontMatterAuthors is not null)
            {
                Progress<FrontMatterAuthorProgress> localProgress = new(value => UpdateCandidateProgress(new(
                    CandidatePreparationPhase.InspectingFinalIdentity, value.CompletedFormats, value.TotalFormats,
                    CandidateProgressUnit.Files, message,
                    $"Selected-book opening pages: {value.CacheHits:N0} cached, {value.Inspections:N0} parser starts")));
                FinalAuthorReviewWorkspace identity = await _inspectFrontMatterAuthors.ExecuteAsync(
                    state, _finalAuthorReviewWorkspace, localProgress, CancellationToken.None,
                    targetBookIds: selectedIds).ConfigureAwait(true);
                identity = PrepareFinalAuthorReviewUseCase.SetAllApply(identity, false);
                ApplyFinalAuthorReviewWorkspace(identity);
            }
            Progress<EditionMetadataEnrichmentProgress> progress = new(value => UpdateCandidateProgress(new(
                CandidatePreparationPhase.ResolvingIdentityRepairs, value.CompletedQueries, value.TotalQueries,
                CandidateProgressUnit.Records, message,
                $"{value.Provider.Id}: {value.CacheHits:N0} cached, {value.ProviderRequests:N0} HTTP requests; {value.Detail}")));
            MetadataReviewWorkspace repairs = await _prepareMetadataReview.ExecuteAsync(
                state, [], progress, CancellationToken.None, _finalAuthorReviewWorkspace, selectedIds).ConfigureAwait(true);
            _identityRepairProviderResults = repairs.ProviderResults;
            ApplyMetadataReviewWorkspace(repairs, state.Snapshot);
            MetadataReviewFilterMode = MetadataReviewFilterMode.All;
            _identityRepairReviewReady = true;
            if (_quarantineReviewWorkspace is not null)
            {
                QuarantineReviewWorkspace assessment = PrepareQuarantineReviewUseCase.ReassessSelected(
                    state, _quarantineReviewWorkspace, _finalAuthorReviewWorkspace,
                    repairs.ProviderResults!, selectedIds, _verifiedManagedNameBookIds);
                ApplyQuarantineReviewWorkspace(assessment);
            }
            int requests = repairs.ProviderResults?.Providers.Sum(value => value.ProviderRequests) ?? 0;
            StatusMessage = $"Repair lookup finished for {scope} only ({requests:N0} HTTP requests). "
                + "Results are in Metadata review, initially unchecked; no matching repair may be available. "
                + "Your quarantine move choices have been kept. "
                + "Review replacements, then use Apply checked identity metadata. Authors follow in Final authors.";
        }

        catch (RegexMatchTimeoutException)
        {
            ReportReadOnlyRegexTimeout();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            ErrorMessage = exception.Message;
            ErrorAction = "Review current evidence and retry read-only identity repair preparation.";
        }
        finally
        {
            await StopCandidateHeartbeatAsync().ConfigureAwait(true);
            IsBusy = false;
            NotifyCandidateCleanupStateChanged();
        }
    }
    private CalibreBookId[] SelectedRepairBookIds()
    {
        if (_quarantineReviewWorkspace is null) return [];
        return _selectedQuarantineReviewSubjects.Length > 0
            ? _selectedQuarantineReviewSubjects.Select(value => value.BookId).OrderBy(value => value.Value).ToArray()
            : SelectedQuarantineReviewSubject is { } current && _allQuarantineReviewSubjects.Contains(current)
                ? [current.BookId] : [];
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
        using IDisposable? operation = _operationCoordinator.TryBegin();
        if (operation is null) return;
        CalibreBookId[] ids = (include ? _quarantineReviewSubjects : _allQuarantineReviewSubjects.AsEnumerable())
            .Where(value => value.Include != include).Select(value => value.BookId).ToArray();
        if (ids.Length == 0) return;
        _quarantineReviewWorkspace = PrepareQuarantineReviewUseCase.SetInclude(
            _quarantineReviewWorkspace,
            ids,
            include);
        ApplyQuarantineReviewWorkspace(_quarantineReviewWorkspace);
        StatusMessage = include
            ? $"Checked {ids.Length:N0} shown book(s) for quarantine. Hidden books were not changed. No files have been moved."
            : $"Unchecked {ids.Length:N0} book(s). No files have been moved.";
    }

    private bool CanSetSelectedQuarantineInclude(bool include) => !IsBusy
        && !_operationCoordinator.IsOperationActive && !_quarantineCompleted
        && _selectedQuarantineReviewSubjects.Any(value => value.Include != include);

    private void SetSelectedQuarantineInclude(bool include)
    {
        if (_quarantineReviewWorkspace is null) return;
        using IDisposable? operation = _operationCoordinator.TryBegin();
        if (operation is null) return;
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
        StatusMessage = $"{(include ? "Checked" : "Unchecked")} {ids.Length:N0} selected book(s) for quarantine. No files have been moved.";
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
            .Where(value => value.IsVisible(ShowAllQuarantineRecords))
            .ToArray();
        _quarantineReviewSubjects.ReplaceAll(visible);
        UpdateQuarantineReviewSelection([]);
        SelectedQuarantineReviewSubject = selectedId is null
            ? visible.FirstOrDefault()
            : visible.FirstOrDefault(value => value.BookId == selectedId.Value) ?? visible.FirstOrDefault();
        int checkedCount = _allQuarantineReviewSubjects.Count(value => value.Include);
        string destinationState = _quarantineReviewWorkspace?.DestinationIdentity?.Exists == true
            ? "existing destination"
            : "new destination";
        QuarantineReviewWorkspace? accounting = _quarantineReviewWorkspace;
        FinalRecordResolutionRoute[] routes = (accounting?.Subjects ?? [])
            .SelectMany(value => value.Disposition.Evidence?.Routes ?? []).ToArray();
        int noEvidence = routes.Count(value => value.Outcome == FinalRecordRouteOutcome.CompletedWithoutEvidence);
        int unavailable = routes.Count(value => value.Outcome == FinalRecordRouteOutcome.Unavailable);
        int deferred = routes.Count(value => value.Outcome == FinalRecordRouteOutcome.DeferredByBounds);
        int httpRequests = _identityRepairProviderResults?.Providers.Sum(value => value.ProviderRequests) ?? 0;
        int problems = _allQuarantineReviewSubjects.Count(value => value.HasDetectedProblem);
        int other = _allQuarantineReviewSubjects.Length - problems;
        QuarantineReviewSummary = $"{problems:N0} book(s) with detected problems; {checkedCount:N0} checked for quarantine. "
            + $"{visible.Length:N0} shown of {_allQuarantineReviewSubjects.Length:N0}. "
            + $"{other:N0} other book(s) have no detected metadata/content problem. "
            + "An incomplete verification does not mean a book is bad.";
        QuarantineVerificationSummary = $"Verification accounting, not a defect count: "
            + $"Retained input {accounting?.RetainedInputCount ?? 0:N0}; qualified {accounting?.QualifiedCount ?? 0:N0}; "
            + $"repairable {accounting?.RepairableCount ?? 0:N0}; pending {accounting?.PendingCount ?? 0:N0}; "
            + $"verified moved {accounting?.VerifiedMovedCount ?? 0:N0}. "
            + $"Local formats cached {_finalAuthorReviewWorkspace?.FrontMatterCacheHitCount ?? 0:N0}, "
            + $"parser starts {_finalAuthorReviewWorkspace?.FrontMatterInspectionCount ?? 0:N0}; HTTP requests {httpRequests:N0}. "
            + $"Routes without usable evidence {noEvidence:N0}, unavailable {unavailable:N0}, deferred {deferred:N0}. "
            + $"{destinationState}. Books hidden from the problem list are still included in these counts.";
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
        QuarantineVerificationSummary = string.Empty;
        _quarantineCompleted = false;
        _identityRepairReviewReady = false;
        _identityRepairProviderResults = null;
        _verifiedManagedNameBookIds.Clear();
        NotifyCandidateCleanupStateChanged();
    }

    private void NotifyQuarantineCommands()
    {
        ClearAllQuarantineIncludeCommand.NotifyCanExecuteChanged();
        CheckAllQuarantineIncludeCommand.NotifyCanExecuteChanged();
        CheckSelectedQuarantineIncludeCommand.NotifyCanExecuteChanged();
        UncheckSelectedQuarantineIncludeCommand.NotifyCanExecuteChanged();
        AddManualQuarantineRecordCommand.NotifyCanExecuteChanged();
        AcceptSelectedQualificationCommand.NotifyCanExecuteChanged();
        ReviewIdentityRepairsCommand.NotifyCanExecuteChanged();
        MoveCheckedQuarantineCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(QuarantineMoveActionText));
    }
}
