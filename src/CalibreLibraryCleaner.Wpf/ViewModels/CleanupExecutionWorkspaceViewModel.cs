using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class CleanupExecutionWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IPrepareCleanupExecution _prepare;
    private readonly IExecuteApprovedCleanupPlan _execute;
    private readonly IExecutionHistoryStore _history;
    private readonly IExecutionBackupFolderPicker _backupFolderPicker;
    private readonly ICleanupExecutionConfirmationService _confirmation;
    private readonly IClock _clock;
    private readonly CleanupPlanWorkspaceViewModel? _cleanupPlans;
    private readonly ObservableCollection<CleanupExecutionHistoryRowViewModel> _historyRows = [];
    private LibrarySnapshot? _snapshot;
    private CleanupPlan? _plan;
    private bool _isImported;
    private string _backupDestination = string.Empty;
    private CleanupExecutionPreparation? _preparation;
    private IReadOnlyList<CleanupExecutionOperationRowViewModel> _operations = [];
    private IReadOnlyList<CleanupExecutionEffectRowViewModel> _effects = [];
    private IReadOnlyList<CleanupExecutionIssueRowViewModel> _issues = [];
    private bool _otherMutatorsClosed;
    private bool _recoveryLimitationsAccepted;
    private bool _isBusy;
    private bool _mutationStarted;
    private string _status = "Select one approved cleanup plan and an external backup folder.";
    private string _progressMessage = string.Empty;
    private double _progressPercentage;
    private string _resultSummary = string.Empty;
    private string _artifactSummary = string.Empty;
    private CancellationTokenSource? _executionCancellation;

    public CleanupExecutionWorkspaceViewModel(
        IPrepareCleanupExecution prepare,
        IExecuteApprovedCleanupPlan execute,
        IExecutionHistoryStore history,
        IExecutionBackupFolderPicker backupFolderPicker,
        ICleanupExecutionConfirmationService confirmation,
        IClock clock,
        CleanupPlanWorkspaceViewModel? cleanupPlans = null)
    {
        _prepare = prepare;
        _execute = execute;
        _history = history;
        _backupFolderPicker = backupFolderPicker;
        _confirmation = confirmation;
        _clock = clock;
        _cleanupPlans = cleanupPlans;
        if (_cleanupPlans is not null) _cleanupPlans.SelectedPlanChanged += OnSelectedPlanChanged;
        History = new ReadOnlyObservableCollection<CleanupExecutionHistoryRowViewModel>(_historyRows);
        ChooseBackupCommand = new RelayCommand(ChooseBackup, () => !IsBusy);
        PrepareCommand = new AsyncRelayCommand(PrepareAsync, CanPrepare);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, CanExecute);
        CancelOrSafeStopCommand = new RelayCommand(RequestStop,
            () => IsBusy && _executionCancellation is { IsCancellationRequested: false });
        RefreshHistoryCommand = new AsyncRelayCommand(RefreshHistoryAsync, () => !IsBusy && _snapshot is not null);
    }

    public IRelayCommand ChooseBackupCommand { get; }
    public IAsyncRelayCommand PrepareCommand { get; }
    public IAsyncRelayCommand ExecuteCommand { get; }
    public IRelayCommand CancelOrSafeStopCommand { get; }
    public IAsyncRelayCommand RefreshHistoryCommand { get; }
    public ReadOnlyObservableCollection<CleanupExecutionHistoryRowViewModel> History { get; }

    public string LibrarySummary => _snapshot is null ? "No scanned library selected."
        : $"{_snapshot.Identity.LibraryRoot} / UUID {_snapshot.Identity.CalibreLibraryUuid}";
    public string PlanSummary => _plan is null ? "No cleanup plan selected."
        : $"Plan {_plan.Id}, revision {_plan.ArtifactRevision.Value}, state {_plan.State}, target {_plan.Definition.TargetRecordId.Value}";
    public string PlanDigest => _plan?.ContentDigest.Value ?? string.Empty;
    public string ImportedApprovalNotice => _isImported
        ? "Imported approval is informational; this execution still requires a new local confirmation."
        : string.Empty;
    public string CompatibilitySummary => _preparation?.Tool is null
        ? "Compatibility has not been proven."
        : $"Trusted calibredb {_preparation.Tool.Identity.ProductVersion}; profile {_preparation.Tool.Identity.CapabilityProfile}; executable SHA-256 {_preparation.Tool.Identity.ExecutableSha256}";

    public string BackupDestination
    {
        get => _backupDestination;
        private set
        {
            if (SetProperty(ref _backupDestination, value))
            {
                InvalidatePreparation("Backup destination changed; prepare again.");
                NotifyCommands();
            }
        }
    }

    public IReadOnlyList<CleanupExecutionOperationRowViewModel> Operations
    {
        get => _operations;
        private set => SetProperty(ref _operations, value);
    }

    public IReadOnlyList<CleanupExecutionIssueRowViewModel> Issues
    {
        get => _issues;
        private set => SetProperty(ref _issues, value);
    }

    public IReadOnlyList<CleanupExecutionEffectRowViewModel> Effects
    {
        get => _effects;
        private set => SetProperty(ref _effects, value);
    }

    public bool OtherMutatorsClosed
    {
        get => _otherMutatorsClosed;
        set { if (SetProperty(ref _otherMutatorsClosed, value)) ExecuteCommand.NotifyCanExecuteChanged(); }
    }

    public bool RecoveryLimitationsAccepted
    {
        get => _recoveryLimitationsAccepted;
        set { if (SetProperty(ref _recoveryLimitationsAccepted, value)) ExecuteCommand.NotifyCanExecuteChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommands();
                CancelOrSafeStopCommand.NotifyCanExecuteChanged();
                RefreshHistoryCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(IsMutationInFlight));
            }
        }
    }

    public bool MutationStarted
    {
        get => _mutationStarted;
        private set
        {
            if (SetProperty(ref _mutationStarted, value))
            {
                OnPropertyChanged(nameof(CancelActionText));
                OnPropertyChanged(nameof(IsMutationInFlight));
            }
        }
    }

    public string CancelActionText => MutationStarted ? "_Stop safely after current operation" : "_Cancel before mutation";
    public bool IsMutationInFlight => IsBusy && MutationStarted;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ProgressMessage { get => _progressMessage; private set => SetProperty(ref _progressMessage, value); }
    public double ProgressPercentage { get => _progressPercentage; private set => SetProperty(ref _progressPercentage, value); }
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }
    public string ArtifactSummary { get => _artifactSummary; private set => SetProperty(ref _artifactSummary, value); }

    public void UpdateSnapshot(LibrarySnapshot? snapshot)
    {
        if (_snapshot?.Identity.CalibreLibraryUuid != snapshot?.Identity.CalibreLibraryUuid
            || !string.Equals(_snapshot?.Identity.LibraryRoot, snapshot?.Identity.LibraryRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            _plan = null;
            _isImported = false;
            InvalidatePreparation("Library changed; select an approved plan and prepare again.");
        }
        _snapshot = snapshot;
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(PlanSummary));
        NotifyCommands();
    }

    public void UpdateContext(LibrarySnapshot? snapshot, CleanupPlan? plan, bool isImported)
    {
        UpdateSnapshot(snapshot);
        OnSelectedPlanChanged(plan, isImported);
    }

    public void Dispose()
    {
        if (_cleanupPlans is not null) _cleanupPlans.SelectedPlanChanged -= OnSelectedPlanChanged;
        _executionCancellation?.Cancel();
        _executionCancellation?.Dispose();
    }

    private void OnSelectedPlanChanged(CleanupPlan? plan, bool isImported)
    {
        _plan = plan;
        _isImported = isImported;
        Effects = plan is null ? [] : CreateEffects(plan);
        InvalidatePreparation("Selected plan changed; live preparation is required.");
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(PlanDigest));
        OnPropertyChanged(nameof(ImportedApprovalNotice));
        NotifyCommands();
    }

    private void ChooseBackup()
    {
        string? selected = _backupFolderPicker.PickBackupFolder(BackupDestination);
        if (selected is not null) BackupDestination = selected;
    }

    private async Task PrepareAsync()
    {
        if (_plan is null || _snapshot is null) return;
        IsBusy = true;
        MutationStarted = false;
        ResultSummary = string.Empty;
        ArtifactSummary = string.Empty;
        try
        {
            Status = "Preparing: fresh read-only scan, exact tool probe, capability mapping, and external destination checks.";
            CleanupExecutionPreparation preparation = await _prepare.ExecuteAsync(new(
                _plan, _snapshot.Identity.LibraryRoot, BackupDestination), null, CancellationToken.None).ConfigureAwait(true);
            _preparation = preparation;
            Operations = preparation.OperationGraph?.Operations.Select(OperationRow).ToArray() ?? [];
            Issues = preparation.Issues.Select(IssueRow).ToArray();
            Status = preparation.IsReady
                ? "Preparation passed. Review every operation, acknowledge the safety conditions, then execute. All checks repeat while holding the lease."
                : "Preparation failed closed. Resolve the blocking issues; no mutation occurred.";
            OnPropertyChanged(nameof(CompatibilitySummary));
        }
        catch (OperationCanceledException)
        {
            Status = "Preparation cancelled; no mutation occurred.";
        }
        finally { IsBusy = false; NotifyCommands(); }
    }

    private async Task ExecuteAsync()
    {
        if (_plan is null || _snapshot is null || _preparation is not { IsReady: true } preparation
            || preparation.Tool is null || preparation.OperationGraph is null
            || preparation.CanonicalLibraryRootIdentity is null
            || preparation.CanonicalBackupDestinationIdentity is null) return;
        if (!_confirmation.ConfirmExecution(preparation))
        {
            Status = "Execution confirmation declined; no mutation occurred.";
            return;
        }

        CleanupExecutionConfirmation confirmation = new(_plan.Id, _plan.ArtifactRevision, _plan.ContentDigest,
            _plan.InputIdentity.LibraryUuid, preparation.CanonicalLibraryRootIdentity,
            preparation.OperationGraph.Digest, preparation.Tool.Identity, preparation.CanonicalBackupDestinationIdentity,
            _clock.GetUtcNow(), OtherMutatorsClosed, RecoveryLimitationsAccepted);
        _executionCancellation?.Dispose();
        _executionCancellation = new CancellationTokenSource();
        IsBusy = true;
        MutationStarted = false;
        ProgressPercentage = 0;
        ResultSummary = string.Empty;
        ArtifactSummary = string.Empty;
        try
        {
            Progress<CleanupExecutionProgress> progress = new(UpdateProgress);
            string version = typeof(CleanupExecutionWorkspaceViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";
            CleanupExecutionResult result = await _execute.ExecuteAsync(new(_plan, _snapshot.Identity.LibraryRoot,
                BackupDestination, confirmation, version), progress, _executionCancellation.Token).ConfigureAwait(true);
            MutationStarted = result.MutationStarted;
            Issues = result.Issues.Select(IssueRow).ToArray();
            ResultSummary = $"{DisplayState(result.State)} / {result.Disposition}. " +
                (result.MutationStarted ? "The mutation boundary was crossed." : "No mutation was authorized.");
            ArtifactSummary = $"Backup bundle: {result.BundlePath ?? "not created"}; journal: {result.JournalIdentity ?? "not created"}; manifest: {result.BackupManifestDigest ?? "not sealed"}.";
            Status = result.IsCompleted
                ? "Completed: final semantic state verified."
                : result.Disposition == CleanupExecutionDisposition.RecoveryRequired
                    ? "Recovery Required: stop using this executor for the library and inspect the durable journal and backup bundle. No rollback was attempted."
                    : "Execution stopped without a verified completed result.";
            await RefreshHistoryAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            _executionCancellation.Dispose();
            _executionCancellation = null;
            NotifyCommands();
        }
    }

    private void RequestStop()
    {
        _executionCancellation?.Cancel();
        Status = MutationStarted
            ? "Safe stop requested. The active Calibre mutation will not be terminated; stopping occurs after its fresh verification."
            : "Cancellation requested. Mutation will not start.";
        CancelOrSafeStopCommand.NotifyCanExecuteChanged();
    }

    private async Task RefreshHistoryAsync()
    {
        if (_snapshot is null) return;
        IReadOnlyList<ExecutionHistoryEntry> entries = await _history.ReadAsync(
            _snapshot.Identity.CalibreLibraryUuid, _snapshot.Identity.LibraryRoot,
            CancellationToken.None).ConfigureAwait(true);
        _historyRows.Clear();
        foreach (ExecutionHistoryEntry entry in entries)
            _historyRows.Add(new(entry.ExecutionId.ToString(), entry.State.ToString(), entry.Disposition.ToString(),
                entry.FailureClassification.ToString(), entry.FinishedAtUtc, entry.MutationStarted, entry.BundlePath));
    }

    private void UpdateProgress(CleanupExecutionProgress value)
    {
        MutationStarted = value.MutationStarted;
        ProgressMessage = value.Message;
        ProgressPercentage = value.TotalOperations == 0 ? 0
            : Math.Clamp(value.CompletedOperations * 100d / value.TotalOperations, 0, 100);
    }

    private bool CanPrepare() => !IsBusy && _snapshot is not null && _plan is { State: CleanupPlanState.Approved }
        && !string.IsNullOrWhiteSpace(BackupDestination);

    private bool CanExecute() => !IsBusy && _preparation is { IsReady: true }
        && _plan is { State: CleanupPlanState.Approved }
        && OtherMutatorsClosed && RecoveryLimitationsAccepted;

    private void NotifyCommands()
    {
        ChooseBackupCommand.NotifyCanExecuteChanged();
        PrepareCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private void InvalidatePreparation(string status)
    {
        _preparation = null;
        Operations = [];
        Issues = [];
        Status = status;
        OnPropertyChanged(nameof(CompatibilitySummary));
    }

    private static CleanupExecutionOperationRowViewModel OperationRow(CleanupExecutionOperation value) => new(
        value.Phase.ToString(), value.Kind.ToString(), value.TargetRecordId.Value, value.SourceRecordId?.Value,
        value.Format ?? string.Empty,
        value.Kind switch
        {
            ExecutionOperationKind.VerifyMetadataPreserved => "Verify target metadata and cover preservation; no command",
            ExecutionOperationKind.VerifyTargetFormatPreserved => "Verify existing target bytes; no command",
            ExecutionOperationKind.AddOrReplaceFormat when value.ReplacesExistingTargetFormat => "Replace explicitly reviewed target format through add_format",
            ExecutionOperationKind.AddOrReplaceFormat => "Add retained format through add_format",
            ExecutionOperationKind.RemoveRedundantRecord => "Remove redundant source record last through non-permanent remove",
            _ => string.Empty,
        },
        string.Join(", ", value.DependencyIds.Select(id => id.Value)));

    private static CleanupExecutionIssueRowViewModel IssueRow(ExecutionIssue value) => new(
        value.Severity.ToString(), value.Code,
        value.RecordId is null ? value.Format ?? "Execution" : $"Record {value.RecordId.Value.Value}{(value.Format is null ? string.Empty : $" / {value.Format}")}",
        value.Explanation);

    private static List<CleanupExecutionEffectRowViewModel> CreateEffects(CleanupPlan plan)
    {
        ExpectedRecordState target = plan.Definition.ExpectedLibraryState.Records.Single(value =>
            value.RecordId == plan.Definition.TargetRecordId);
        List<CleanupExecutionEffectRowViewModel> values = plan.Definition.FormatRetentions
            .Where(value => value.Mode == FormatRetentionMode.RetainFromOtherRecord)
            .Select(value => new CleanupExecutionEffectRowViewModel(
                target.Formats.Any(format => format.Format == value.Format) ? "Replace format" : "Add format",
                target.RecordId.Value,
                value.Format,
                $"Retain verified bytes from record {value.SourceState.RecordId.Value} through add_format."))
            .ToList();
        values.AddRange(plan.Definition.FormatRemovals.Select(value => new CleanupExecutionEffectRowViewModel(
            "Remove format",
            value.RecordId.Value,
            value.Format,
            value.RecordId == target.RecordId
                ? "Existing destination bytes are replaced by the explicitly selected retained format through add_format."
                : "The format leaves the active library only with its redundant source record; no standalone format-delete command is used.")));
        values.AddRange(plan.Definition.RecordRemovals.Select(value => new CleanupExecutionEffectRowViewModel(
            "Remove record",
            value.RecordId.Value,
            string.Empty,
            "Remove the redundant source record last through non-permanent Calibre remove.")));
        return values;
    }

    private static string DisplayState(CleanupExecutionState state) => state switch
    {
        CleanupExecutionState.Completed => "Completed",
        CleanupExecutionState.ExecutionPartiallyApplied => "Partially Applied",
        CleanupExecutionState.VerificationFailed => "Verification Failed",
        CleanupExecutionState.RecoveryRequired => "Recovery Required",
        CleanupExecutionState.CancelledBeforeMutation => "Cancelled Before Mutation",
        _ when state is CleanupExecutionState.PreflightFailed or CleanupExecutionState.BackupFailed or CleanupExecutionState.ExecutionFailedBeforeMutation => "Failed",
        _ => state.ToString(),
    };
}
