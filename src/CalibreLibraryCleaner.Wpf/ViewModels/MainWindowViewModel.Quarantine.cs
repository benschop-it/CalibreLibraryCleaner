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
    private QuarantineReviewFilterMode _quarantineReviewFilterMode = QuarantineReviewFilterMode.All;
    private string _quarantineReviewSummary = "Complete final author and managed-name cleanup, then prepare quarantine review.";
    private string _quarantineDestinationPath = string.Empty;
    private string _quarantineManualRecordId = string.Empty;
    private bool _finalAuthorStageCompleted;
    private bool _quarantineCompleted;
    private bool _identityRepairReviewReady;
    private EditionMetadataProvidersBatchResult? _identityRepairProviderResults;
    private HashSet<CalibreBookId> _verifiedManagedNameBookIds = [];

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
    public IRelayCommand AcceptSelectedQualificationCommand { get; }
    public IAsyncRelayCommand ReviewIdentityRepairsCommand { get; }

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
        if (_inspectFrontMatterAuthors is not null)
        {
            UpdateCandidateProgress(new(
                CandidatePreparationPhase.InspectingFinalIdentity, 0, 0, CandidateProgressUnit.Files,
                "Inspecting retained identity"));
            Progress<FrontMatterAuthorProgress> progress = new(value => UpdateCandidateProgress(new(
                CandidatePreparationPhase.InspectingFinalIdentity, value.CompletedFormats, value.TotalFormats,
                CandidateProgressUnit.Files, "Inspecting retained identity",
                $"{value.CacheHits:N0} cached, {value.Inspections:N0} parser starts")));
            FinalAuthorReviewWorkspace identity = await _inspectFrontMatterAuthors.ExecuteAsync(
                state, _finalAuthorReviewWorkspace, progress,
                _scanCancellation?.Token ?? CancellationToken.None, inspectAllRecords: true).ConfigureAwait(true);
            identity = PrepareFinalAuthorReviewUseCase.SetAllApply(identity, false);
            ApplyFinalAuthorReviewWorkspace(identity);
        }
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
            StatusMessage = _quarantineReviewWorkspace.IsHighQualityComplete
                ? "High-quality library complete: all retained records are qualified or verified moved."
                : $"No quarantine records are checked. {_quarantineReviewWorkspace.PendingCount:N0} records remain pending; "
                    + "the source is not yet a finished high-quality library.";
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
            CalibreBookId[] moved = _quarantineReviewWorkspace.Subjects
                .Where(value => value.Include).Select(value => value.BookId).ToArray();
            if (result.RemovedSourceRecordCount != moved.Length || result.CopiedRecordCount != moved.Length)
                throw new InvalidOperationException("Completed quarantine returned inconsistent record accounting.");
            QuarantineReviewWorkspace accounting = PrepareQuarantineReviewUseCase.RecordVerifiedMoves(
                _quarantineReviewWorkspace, moved);
            _quarantineCompleted = true;
            ApplyQuarantineReviewWorkspace(accounting);
            StatusMessage = accounting.IsHighQualityComplete
                ? "High-quality library complete: retained records equal qualified records remaining plus verified moves."
                : $"Unresolvable transfer completed; {accounting.PendingCount:N0} records remain pending. "
                    + "The source is not yet a finished high-quality library. Rescan before another mutation.";
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
        using IDisposable? operation = _operationCoordinator.TryBegin();
        if (operation is null) return;
        IsBusy = true;
        ClearError();
        StartCandidateHeartbeat();
        try
        {
            UpdateCandidateProgress(new(
                CandidatePreparationPhase.ResolvingIdentityRepairs, 0, 0, CandidateProgressUnit.Records,
                "Resolving bounded identity repairs"));
            Progress<EditionMetadataEnrichmentProgress> progress = new(value => UpdateCandidateProgress(new(
                CandidatePreparationPhase.ResolvingIdentityRepairs, value.CompletedQueries, value.TotalQueries,
                CandidateProgressUnit.Records, "Resolving bounded identity repairs",
                $"{value.Provider.Id}: {value.CacheHits:N0} cached, {value.ProviderRequests:N0} HTTP requests; {value.Detail}")));
            MetadataReviewWorkspace repairs = await _prepareMetadataReview.ExecuteAsync(
                state, [], progress, CancellationToken.None, _finalAuthorReviewWorkspace).ConfigureAwait(true);
            _identityRepairProviderResults = repairs.ProviderResults;
            repairs = await _prepareMetadataReview.SetAllApplyAsync(
                repairs, false, CancellationToken.None).ConfigureAwait(true);
            ApplyMetadataReviewWorkspace(repairs, state.Snapshot);
            _identityRepairReviewReady = true;
            if (_quarantineReviewWorkspace is not null)
            {
                QuarantineReviewWorkspace assessment = PrepareQuarantineReviewUseCase.Execute(
                    state, _finalAuthorReviewWorkspace, managedLayoutVerified: true,
                    _identityRepairProviderResults, _verifiedManagedNameBookIds);
                assessment = PrepareQuarantineReviewUseCase.BindDestination(
                    assessment, _quarantineReviewWorkspace.DestinationIdentity!);
                ApplyQuarantineReviewWorkspace(assessment);
            }
            StatusMessage = "Identity repairs are ready in Metadata review, initially unchecked. "
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
        QuarantineReviewWorkspace? accounting = _quarantineReviewWorkspace;
        FinalRecordResolutionRoute[] routes = (accounting?.Subjects ?? [])
            .SelectMany(value => value.Disposition.Evidence?.Routes ?? []).ToArray();
        int noEvidence = routes.Count(value => value.Outcome == FinalRecordRouteOutcome.CompletedWithoutEvidence);
        int unavailable = routes.Count(value => value.Outcome == FinalRecordRouteOutcome.Unavailable);
        int deferred = routes.Count(value => value.Outcome == FinalRecordRouteOutcome.DeferredByBounds);
        int httpRequests = _identityRepairProviderResults?.Providers.Sum(value => value.ProviderRequests) ?? 0;
        QuarantineReviewSummary = $"{visible.Length:N0} of {_allQuarantineReviewSubjects.Length:N0} records visible; "
            + $"checked {checkedCount:N0}, manually included {manualCount:N0}; {destinationState}. "
            + $"Retained input {accounting?.RetainedInputCount ?? 0:N0}; qualified {accounting?.QualifiedCount ?? 0:N0}; "
            + $"repairable {accounting?.RepairableCount ?? 0:N0}; pending {accounting?.PendingCount ?? 0:N0}; "
            + $"verified moved {accounting?.VerifiedMovedCount ?? 0:N0}. "
            + $"Local formats cached {_finalAuthorReviewWorkspace?.FrontMatterCacheHitCount ?? 0:N0}, "
            + $"parser starts {_finalAuthorReviewWorkspace?.FrontMatterInspectionCount ?? 0:N0}; HTTP requests {httpRequests:N0}. "
            + $"Routes without usable evidence {noEvidence:N0}, unavailable {unavailable:N0}, deferred {deferred:N0}.";
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
    }
}
