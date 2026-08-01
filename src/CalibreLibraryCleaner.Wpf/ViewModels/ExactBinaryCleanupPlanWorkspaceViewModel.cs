using System.ComponentModel;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class ExactBinaryCleanupPlanWorkspaceViewModel : ObservableObject
{
    private readonly GenerateExactBinaryCleanupPlanUseCase _generate;
    private readonly ValidateExactBinaryCleanupPlanUseCase _validate;
    private readonly ApproveExactBinaryCleanupPlanUseCase _approve;
    private readonly PrepareExactBinaryRecordDeletionUseCase _prepare;
    private readonly ExecuteExactBinaryRecordDeletionUseCase _execute;
    private readonly IExecutionBackupFolderPicker _backupFolderPicker;
    private readonly IExactBinaryCleanupPlanConfirmationService _confirmation;
    private readonly Dictionary<string, ExactBinaryCleanupPlan> _plans = new(StringComparer.Ordinal);
    private LibrarySnapshot? _snapshot;
    private ExactDuplicateGroupRowViewModel? _group;
    private ExactDuplicateMemberRowViewModel? _retained;
    private ExactBinaryCleanupPlan? _plan;
    private string _status = "Select one exact duplicate file to keep.";
    private string _backupDestination = string.Empty;
    private ExactBinaryRecordDeletionPreparation? _preparation;
    private bool _otherMutatorsClosed;
    private bool _fullLibraryCopyAcknowledged;
    private bool _isBusy;
    private double _progressPercentage;
    private string _progressMessage = string.Empty;
    private string _resultSummary = string.Empty;

    public ExactBinaryCleanupPlanWorkspaceViewModel(
        GenerateExactBinaryCleanupPlanUseCase generate,
        ValidateExactBinaryCleanupPlanUseCase validate,
        ApproveExactBinaryCleanupPlanUseCase approve,
        PrepareExactBinaryRecordDeletionUseCase prepare,
        ExecuteExactBinaryRecordDeletionUseCase execute,
        IExecutionBackupFolderPicker backupFolderPicker,
        IExactBinaryCleanupPlanConfirmationService confirmation)
    {
        _generate = generate;
        _validate = validate;
        _approve = approve;
        _prepare = prepare;
        _execute = execute;
        _backupFolderPicker = backupFolderPicker;
        _confirmation = confirmation;
        GenerateCommand = new RelayCommand(Generate, CanGenerate);
        ValidateCommand = new RelayCommand(Validate, () => _snapshot is not null && Plan is not null);
        ApproveCommand = new RelayCommand(Approve,
            () => _snapshot is not null && Plan is { State: CleanupPlanState.Valid } && CurrentSelectionsMatchPlan());
        ChooseBackupCommand = new RelayCommand(ChooseBackup, () => !IsBusy);
        PrepareCommand = new AsyncRelayCommand(PrepareAsync, CanPrepare);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, CanExecute);
    }

    public IRelayCommand GenerateCommand { get; }
    public IRelayCommand ValidateCommand { get; }
    public IRelayCommand ApproveCommand { get; }
    public IRelayCommand ChooseBackupCommand { get; }
    public IAsyncRelayCommand PrepareCommand { get; }
    public IAsyncRelayCommand ExecuteCommand { get; }

    public ExactBinaryCleanupPlan? Plan
    {
        get => _plan;
        private set
        {
            if (SetProperty(ref _plan, value))
            {
                InvalidatePreparation();
                RefreshPlanProperties();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string PlanSummary => Plan is null
        ? "No duplicate-record cleanup plan exists for this group."
        : $"Plan {Plan.Id} / {Plan.State}: keep record {Plan.Definition.RetainedFormat.RecordId.Value} " +
                    $"and delete {Plan.Definition.RecordIdsToRemove.Count} explicitly marked duplicate record(s).";

    public string PlanDigest => Plan?.ContentDigest.Value ?? string.Empty;

    public string IssueSummary => Plan is null
        ? string.Empty
        : string.Join(" ", Plan.Validation.Issues.Select(value => $"{value.Code}: {value.Explanation}"));

    public string BackupDestination
    {
        get => _backupDestination;
        private set
        {
            if (SetProperty(ref _backupDestination, value))
            {
                InvalidatePreparation();
                NotifyCommands();
            }
        }
    }

    public bool OtherMutatorsClosed
    {
        get => _otherMutatorsClosed;
        set { if (SetProperty(ref _otherMutatorsClosed, value)) ExecuteCommand.NotifyCanExecuteChanged(); }
    }

    public bool FullLibraryCopyAcknowledged
    {
        get => _fullLibraryCopyAcknowledged;
        set { if (SetProperty(ref _fullLibraryCopyAcknowledged, value)) ExecuteCommand.NotifyCanExecuteChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) NotifyCommands();
        }
    }

    public double ProgressPercentage
    {
        get => _progressPercentage;
        private set => SetProperty(ref _progressPercentage, value);
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        private set => SetProperty(ref _progressMessage, value);
    }

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    public void UpdateContext(
        LibrarySnapshot? snapshot,
        ExactDuplicateGroupRowViewModel? group,
        ExactDuplicateMemberRowViewModel? retained)
    {
        if (!ReferenceEquals(_group, group))
        {
            if (_group is not null) _group.PropertyChanged -= OnGroupPropertyChanged;
            if (group is not null) group.PropertyChanged += OnGroupPropertyChanged;
        }
        _snapshot = snapshot;
        _group = group;
        _retained = retained;
        Plan = group is not null && _plans.TryGetValue(group.Id, out ExactBinaryCleanupPlan? existing)
            ? existing
            : null;
        Status = snapshot is null
            ? "Run a fresh scan before creating a duplicate-record cleanup plan."
            : retained is null
                ? "Select one exact duplicate file to keep."
                : Plan is not null && Plan.Definition.RetainedFormat.RecordId == retained.Member.BookId
                    && Plan.Definition.RetainedFormat.Format == retained.Member.Format
                    && Plan.Definition.RetainedFormat.RelativePath == retained.Member.ExpectedRelativePath.Replace('\\', '/')
                    ? $"Current {Plan.State} plan matches the selected keeper."
                    : group?.MarkedRecordIds.Count > 0
                        ? "Keeper and deletion records selected. Create a cleanup plan."
                        : "Select at least one duplicate record for deletion.";
        NotifyCommands();
    }

    public void ReconcileAfterSuccessfulScan(LibrarySnapshot snapshot)
    {
        _snapshot = snapshot;
        foreach ((string groupId, ExactBinaryCleanupPlan plan) in _plans.ToArray())
        {
            ExactBinaryCleanupPlanOperationOutcome outcome = _validate.Execute(plan, snapshot);
            if (outcome.Plan is not null) _plans[groupId] = outcome.Plan;
        }
        if (_group is not null && _plans.TryGetValue(_group.Id, out ExactBinaryCleanupPlan? selected))
            Plan = selected;
        NotifyCommands();
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(ExactDuplicateGroupRowViewModel.MarkedRecordIds)) return;
        InvalidatePreparation();
        Status = _group?.MarkedRecordIds.Count > 0
            ? "Deletion selection changed. Create a new cleanup plan."
            : "Select at least one duplicate record for deletion.";
        NotifyCommands();
    }

    private void Generate()
    {
        if (_snapshot is null || _group is null || _retained is null) return;
        ExactBinaryCleanupPlanGenerationOutcome outcome = _generate.Execute(
            _snapshot, _group.GroupId, _retained.Member, _group.MarkedRecordIds);
        if (outcome.Plan is null)
        {
            Status = string.Join(" ", outcome.Validation.BlockingErrors.Select(value => value.Explanation));
            return;
        }
        _plans[_group.Id] = outcome.Plan;
        Plan = outcome.Plan;
        Status = "Valid duplicate-record cleanup plan created in memory. No Calibre change or backup occurred.";
        NotifyCommands();
    }

    private void ChooseBackup()
    {
        string? selected = _backupFolderPicker.PickBackupFolder(BackupDestination);
        if (selected is not null) BackupDestination = selected;
    }

    private async Task PrepareAsync()
    {
        if (_snapshot is null || Plan is null) return;
        IsBusy = true;
        ResultSummary = string.Empty;
        try
        {
            Status = "Running a fresh scan, checking Calibre, and validating the external backup destination.";
            _preparation = await _prepare.ExecuteAsync(new(Plan, _snapshot.Identity.LibraryRoot, BackupDestination),
                null, CancellationToken.None).ConfigureAwait(true);
            Status = _preparation.IsReady
                ? "Preflight passed. Confirm the two acknowledgements, then execute the marked deletions."
                : string.Join(" ", _preparation.Issues.Select(value => $"{value.Code}: {value.Explanation}"));
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private async Task ExecuteAsync()
    {
        if (_snapshot is null || Plan is null || _preparation is not { IsReady: true }) return;
        IsBusy = true;
        ProgressPercentage = 0;
        ResultSummary = string.Empty;
        try
        {
            Progress<ExactBinaryRecordDeletionProgress> progress = new(value =>
            {
                ProgressMessage = value.Message;
                ProgressPercentage = value.TotalRecords == 0 ? 0
                    : 100d * value.CompletedRecords / value.TotalRecords;
            });
            string version = typeof(ExactBinaryCleanupPlanWorkspaceViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";
            ExactBinaryRecordDeletionResult result = await _execute.ExecuteAsync(new(
                Plan, _snapshot.Identity.LibraryRoot, BackupDestination, version,
                OtherMutatorsClosed, FullLibraryCopyAcknowledged), progress, CancellationToken.None).ConfigureAwait(true);
            ResultSummary = $"{result.State}: removed {result.RemovedRecordCount} marked record(s). " +
                $"Backup bundle: {result.BundlePath ?? "not created"}.";
            Status = result.IsCompleted
                ? "Deletion completed and verified. Run a fresh library scan to refresh all duplicate groups."
                : string.Join(" ", result.Issues.Select(value => $"{value.Code}: {value.Explanation}"));
        }
        finally
        {
            IsBusy = false;
            InvalidatePreparation();
            NotifyCommands();
        }
    }

    private void Validate()
    {
        if (_snapshot is null || Plan is null) return;
        ExactBinaryCleanupPlanOperationOutcome outcome = _validate.Execute(Plan, _snapshot);
        if (outcome.Plan is not null)
        {
            Plan = outcome.Plan;
            _plans[outcome.Plan.Definition.GroupId.Value] = outcome.Plan;
        }
        Status = Plan?.State == CleanupPlanState.Stale
            ? "The duplicate-record cleanup plan is stale. Select a keeper and create a new plan."
            : "Duplicate-record cleanup plan validated against the current scan.";
        NotifyCommands();
    }

    private void Approve()
    {
        if (_snapshot is null || Plan is null || !_confirmation.ConfirmApproval(Plan)) return;
        ExactBinaryCleanupPlanOperationOutcome outcome = _approve.Execute(Plan, _snapshot);
        if (outcome.Plan is null)
        {
            Status = "Approval was blocked because the plan is no longer current and valid.";
            return;
        }
        Plan = outcome.Plan;
        _plans[outcome.Plan.Definition.GroupId.Value] = outcome.Plan;
        Status = "Marked duplicate-record removals approved. No Calibre change or backup occurred.";
        NotifyCommands();
    }

    private bool CanGenerate() => _snapshot is not null && _group is not null && _retained is not null
        && _group.MarkedRecordIds.Count > 0;

    private bool CanPrepare() => !IsBusy && _snapshot is not null
        && Plan is { State: CleanupPlanState.Approved }
        && CurrentSelectionsMatchPlan()
        && !string.IsNullOrWhiteSpace(BackupDestination);

    private bool CanExecute() => !IsBusy && _preparation is { IsReady: true }
        && Plan is { State: CleanupPlanState.Approved }
        && OtherMutatorsClosed && FullLibraryCopyAcknowledged;

    private bool CurrentSelectionsMatchPlan() => Plan is not null && _group is not null && _retained is not null
        && Plan.Definition.RetainedFormat.RecordId == _retained.Member.BookId
        && Plan.Definition.RetainedFormat.Format == _retained.Member.Format
        && Plan.Definition.RetainedFormat.RelativePath == _retained.Member.ExpectedRelativePath.Replace('\\', '/')
        && Plan.Definition.RecordIdsToRemove.SequenceEqual(_group.MarkedRecordIds);

    private void NotifyCommands()
    {
        GenerateCommand.NotifyCanExecuteChanged();
        ValidateCommand.NotifyCanExecuteChanged();
        ApproveCommand.NotifyCanExecuteChanged();
        ChooseBackupCommand.NotifyCanExecuteChanged();
        PrepareCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private void InvalidatePreparation() => _preparation = null;

    private void RefreshPlanProperties()
    {
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(PlanDigest));
        OnPropertyChanged(nameof(IssueSummary));
    }
}
