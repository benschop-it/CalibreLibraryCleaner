using System.ComponentModel;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed partial class ExactBinaryCleanupPlanWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly ExecuteBulkExactDuplicateCleanupUseCase _execute;
    private readonly IExactDuplicateCleanupConfirmationService _confirmation;
    private readonly ILibraryStateSession _libraryState;
    private readonly IClock _clock;
    private readonly LibraryOperationCoordinator _operationCoordinator;
    private readonly ILogger<ExactBinaryCleanupPlanWorkspaceViewModel> _logger;
    private LibrarySnapshot? _snapshot;
    private LibraryState? _state;
    private IReadOnlyList<ExactDuplicateGroupRowViewModel> _groups = [];
    private string _status = "Run a scan to find exact file duplicates.";
    private string _progressMessage = string.Empty;
    private string _resultSummary = string.Empty;
    private double _progressPercentage;
    private bool _isBusy;

    public ExactBinaryCleanupPlanWorkspaceViewModel(
        ExecuteBulkExactDuplicateCleanupUseCase execute,
        IExactDuplicateCleanupConfirmationService confirmation,
        ILibraryStateSession libraryState,
        IClock clock,
        LibraryOperationCoordinator? operationCoordinator = null,
        ILogger<ExactBinaryCleanupPlanWorkspaceViewModel>? logger = null)
    {
        _execute = execute;
        _confirmation = confirmation;
        _libraryState = libraryState;
        _clock = clock;
        _operationCoordinator = operationCoordinator ?? new();
        _logger = logger ?? NullLogger<ExactBinaryCleanupPlanWorkspaceViewModel>.Instance;
        _operationCoordinator.StateChanged += OnOperationStateChanged;
        RemoveDuplicatesCommand = new AsyncRelayCommand(RemoveDuplicatesAsync, CanRemoveDuplicates);
    }

    public IAsyncRelayCommand RemoveDuplicatesCommand { get; }

    public string Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value)) LogUserVisibleExactStatus(_logger, value);
        }
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        private set
        {
            if (SetProperty(ref _progressMessage, value) && value.Length > 0)
                LogUserVisibleExactProgress(_logger, value);
        }
    }

    public string ResultSummary
    {
        get => _resultSummary;
        private set
        {
            if (SetProperty(ref _resultSummary, value) && value.Length > 0)
                LogUserVisibleExactResult(_logger, value);
        }
    }

    public double ProgressPercentage
    {
        get => _progressPercentage;
        private set => SetProperty(ref _progressPercentage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RemoveDuplicatesCommand.NotifyCanExecuteChanged();
        }
    }

    public void UpdateContext(
        LibrarySnapshot? snapshot,
        IReadOnlyList<ExactDuplicateGroupRowViewModel> groups)
    {
        foreach (ExactDuplicateGroupRowViewModel group in _groups)
            group.PropertyChanged -= OnGroupPropertyChanged;
        _snapshot = snapshot;
        _state = snapshot is null ? null : _libraryState.GetCurrent(snapshot.Identity.LibraryRoot);
        _groups = groups;
        foreach (ExactDuplicateGroupRowViewModel group in groups)
            group.PropertyChanged += OnGroupPropertyChanged;
        int eligible = groups.Count(value => !value.Skip && value.IsCleanupEligible && value.RetainedMember is not null);
        Status = WorkflowStatus(snapshot, _state, eligible);
        RemoveDuplicatesCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => _operationCoordinator.StateChanged -= OnOperationStateChanged;

    private bool CanRemoveDuplicates() => !IsBusy && !_operationCoordinator.IsOperationActive
        && _snapshot is not null
        && _state is { IsWorkflowCheckpointCurrent: true }
        && _state.WorkflowCheckpoint.Phase == LibraryWorkflowPhase.ExactReady;

    private async Task RemoveDuplicatesAsync()
    {
        if (_snapshot is null) return;
        using IDisposable? operation = _operationCoordinator.TryBegin();
        if (operation is null) return;
        ExactDuplicateKeeperSelection[] selections = _groups
            .Where(value => !value.Skip && value.IsCleanupEligible && value.RetainedMember is not null)
            .Select(value => new ExactDuplicateKeeperSelection(value.GroupId, value.RetainedMember!.Member))
            .ToArray();
        if (!_confirmation.ConfirmExternalBackup(selections.Length))
        {
            Status = "Duplicate cleanup canceled. Confirm a complete external backup before retrying.";
            return;
        }
        IsBusy = true;
        ProgressPercentage = 0;
        ProgressMessage = string.Empty;
        ResultSummary = string.Empty;
        try
        {
            Progress<BulkExactDuplicateCleanupProgress> progress = new(value =>
            {
                ProgressMessage = value.Message;
                ProgressPercentage = value.TotalOperations == 0 ? 0
                    : 100d * value.CompletedOperations / value.TotalOperations;
            });
            BulkExactDuplicateCleanupResult result = await _execute.ExecuteAsync(new(
                _snapshot.Identity.LibraryRoot, selections, ExternalBackupConfirmed: true),
                progress, CancellationToken.None).ConfigureAwait(true);
            ResultSummary = $"Removed {result.RemovedFormatCount:N0} duplicate format(s), merged "
                + $"{result.MergedRecordCount:N0} record(s), and deleted {result.RemovedRecordCount:N0} empty record(s)."
                + (result.SkippedRecordCount > 0
                    ? $" {result.SkippedRecordCount:N0} ambiguous or conflicting record(s) were left unchanged."
                    : string.Empty);
            if (result.IsCompleted)
            {
                LibraryState? current = _libraryState.GetCurrent(_snapshot.Identity.LibraryRoot);
                DateTimeOffset publishedAt = _clock.GetUtcNow().ToUniversalTime();
                if (current is not null && publishedAt < current.ProjectedAtUtc)
                    publishedAt = current.ProjectedAtUtc;
                LibraryStateSessionOutcome advanced = await _libraryState.AdvanceWorkflowAsync(
                    _snapshot.Identity.LibraryRoot,
                    LibraryWorkflowPhase.CandidatePreparationReady,
                    publishedAt,
                    CancellationToken.None).ConfigureAwait(true);
                Status = advanced.IsSuccess
                    ? result.State == BulkExactDuplicateCleanupState.NothingToDo
                        ? "Exact cleanup completed; no library changes were required. Candidate preparation is available."
                        : "Exact cleanup completed. Candidate preparation is available."
                    : advanced.Explanation
                        ?? "Exact cleanup completed, but its workflow checkpoint could not be persisted.";
            }
            else
            {
                Status = string.Join(" ", result.Issues.Select(value => $"{value.Code}: {value.Explanation}"));
            }
        }
        finally
        {
            IsBusy = false;
            _state = _snapshot is null ? null : _libraryState.GetCurrent(_snapshot.Identity.LibraryRoot);
            RemoveDuplicatesCommand.NotifyCanExecuteChanged();
        }
    }

    private static string WorkflowStatus(
        LibrarySnapshot? snapshot,
        LibraryState? state,
        int eligible) => (snapshot, state) switch
        {
            (null, _) => "Run exact-only analysis before Exact cleanup.",
            (_, null) => "No authoritative staged workflow state is loaded.",
            (_, { IsAuthoritative: false }) =>
                "Library state is uncertain. Run a new exact analysis before cleanup.",
            (_, { IsWorkflowCheckpointCurrent: false }) =>
                "The workflow checkpoint does not match current authoritative state.",
            (_, { WorkflowCheckpoint.Phase: LibraryWorkflowPhase.RequiresExactAnalysis }) =>
                "Run a new exact analysis before Exact cleanup.",
            (_, { WorkflowCheckpoint.Phase: LibraryWorkflowPhase.ExactReady }) when eligible == 0 =>
                "No selected exact duplicate groups require changes. Run Exact cleanup to complete this stage.",
            (_, { WorkflowCheckpoint.Phase: LibraryWorkflowPhase.ExactReady }) =>
                $"{eligible:N0} exact duplicate group(s) are ready. Select another row to change any keeper, then run Exact cleanup.",
            _ => "Exact cleanup is complete for this workflow generation.",
        };

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ExactDuplicateGroupRowViewModel.Skip)
            or nameof(ExactDuplicateGroupRowViewModel.RetainedMember))
            UpdateContext(_snapshot, _groups);
    }

    private void OnOperationStateChanged(object? sender, EventArgs eventArgs) =>
        RemoveDuplicatesCommand.NotifyCanExecuteChanged();

    [LoggerMessage(EventId = 920, EventName = "UserVisibleExactStatus", Level = LogLevel.Information, Message = "{Message}")]
    private static partial void LogUserVisibleExactStatus(ILogger logger, string message);

    [LoggerMessage(EventId = 921, EventName = "UserVisibleExactProgress", Level = LogLevel.Information, Message = "{Message}")]
    private static partial void LogUserVisibleExactProgress(ILogger logger, string message);

    [LoggerMessage(EventId = 922, EventName = "UserVisibleExactResult", Level = LogLevel.Information, Message = "{Message}")]
    private static partial void LogUserVisibleExactResult(ILogger logger, string message);
}
