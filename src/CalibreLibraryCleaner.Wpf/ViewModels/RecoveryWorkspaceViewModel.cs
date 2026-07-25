using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recoveries;
using CalibreLibraryCleaner.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class RecoveryWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly InspectRecoverySourceExecutionUseCase _inspect;
    private readonly ReconcileCurrentRecoveryStateUseCase _reconcile;
    private readonly EvaluateRecoveryEligibilityUseCase _eligibility;
    private readonly GenerateRecoveryPlanUseCase _generate;
    private readonly ApproveRecoveryPlanUseCase _approve;
    private readonly ExportRecoveryArtifactsUseCase _export;
    private readonly IPrepareRecoveryExecution _prepare;
    private readonly IExecuteApprovedRecoveryPlan _execute;
    private readonly ICalibreToolDiscovery _toolDiscovery;
    private readonly ICalibreExecutionProfileProvider _profiles;
    private readonly IRecoverySourceFolderPicker _sourcePicker;
    private readonly IExecutionBackupFolderPicker _backupPicker;
    private readonly IRecoveryPlanFilePicker _planPicker;
    private readonly IRecoveryWorkflowConfirmationService _confirmation;
    private readonly IClock _clock;
    private LibrarySnapshot? _snapshot;
    private RecoverySourceInspection? _source;
    private RecoveryCurrentStateSnapshot? _current;
    private CurrentStateReconciliation? _currentReconciliation;
    private RecoveryPlan? _plan;
    private CalibreToolDescriptor? _tool;
    private RecoveryCapabilityProfile? _profile;
    private RecoveryExecutionPreparation? _preparation;
    private CancellationTokenSource? _cancellation;
    private string _sourceBundle = string.Empty;
    private string _backupDestination = string.Empty;
    private string _status = "Select one recoverable Milestone 7 execution bundle.";
    private string _progress = string.Empty;
    private string _result = string.Empty;
    private string _artifactStatus = string.Empty;
    private bool _otherMutatorsClosed;
    private bool _safeBoundaryUnderstood;
    private bool _isBusy;
    private bool _mutationStarted;
    private double _progressPercent;
    private IReadOnlyList<RecoveryIssueRowViewModel> _issues = [];
    private IReadOnlyList<RecoveryReconciliationRowViewModel> _records = [];
    private IReadOnlyList<RecoveryOperationRowViewModel> _operations = [];
    private IReadOnlyList<RecoveryPreservationRowViewModel> _preserved = [];
    private IReadOnlyList<RecoveryRecordMappingRowViewModel> _mappings = [];

    public RecoveryWorkspaceViewModel(
        InspectRecoverySourceExecutionUseCase inspect,
        ReconcileCurrentRecoveryStateUseCase reconcile,
        EvaluateRecoveryEligibilityUseCase eligibility,
        GenerateRecoveryPlanUseCase generate,
        ApproveRecoveryPlanUseCase approve,
        ExportRecoveryArtifactsUseCase export,
        IPrepareRecoveryExecution prepare,
        IExecuteApprovedRecoveryPlan execute,
        ICalibreToolDiscovery toolDiscovery,
        ICalibreExecutionProfileProvider profiles,
        IRecoverySourceFolderPicker sourcePicker,
        IExecutionBackupFolderPicker backupPicker,
        IRecoveryPlanFilePicker planPicker,
        IRecoveryWorkflowConfirmationService confirmation,
        IClock clock)
    {
        _inspect = inspect;
        _reconcile = reconcile;
        _eligibility = eligibility;
        _generate = generate;
        _approve = approve;
        _export = export;
        _prepare = prepare;
        _execute = execute;
        _toolDiscovery = toolDiscovery;
        _profiles = profiles;
        _sourcePicker = sourcePicker;
        _backupPicker = backupPicker;
        _planPicker = planPicker;
        _confirmation = confirmation;
        _clock = clock;
        SelectSourceCommand = new AsyncRelayCommand(SelectSourceAsync, () => !IsBusy);
        ReconcileAndPlanCommand = new AsyncRelayCommand(ReconcileAndPlanAsync, CanReconcile);
        ApprovePlanCommand = new RelayCommand(ApprovePlan, CanApprove);
        ExportPlanCommand = new AsyncRelayCommand(ExportPlanAsync, CanExport);
        ChooseBackupCommand = new RelayCommand(ChooseBackup, () => !IsBusy);
        PrepareCommand = new AsyncRelayCommand(PrepareAsync, CanPrepare);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, CanExecute);
        CancelOrSafeStopCommand = new RelayCommand(RequestStop,
            () => IsBusy && _cancellation is { IsCancellationRequested: false });
    }

    public IAsyncRelayCommand SelectSourceCommand { get; }
    public IAsyncRelayCommand ReconcileAndPlanCommand { get; }
    public IRelayCommand ApprovePlanCommand { get; }
    public IAsyncRelayCommand ExportPlanCommand { get; }
    public IRelayCommand ChooseBackupCommand { get; }
    public IAsyncRelayCommand PrepareCommand { get; }
    public IAsyncRelayCommand ExecuteCommand { get; }
    public IRelayCommand CancelOrSafeStopCommand { get; }

    public string SourceBundle { get => _sourceBundle; private set => SetProperty(ref _sourceBundle, value); }
    public string BackupDestination { get => _backupDestination; private set => SetProperty(ref _backupDestination, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ProgressMessage { get => _progress; private set => SetProperty(ref _progress, value); }
    public string ResultSummary { get => _result; private set => SetProperty(ref _result, value); }
    public string ArtifactStatus { get => _artifactStatus; private set => SetProperty(ref _artifactStatus, value); }
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
    public string LibrarySummary => _snapshot is null ? "Scan and select the affected library first."
        : $"{_snapshot.Identity.LibraryRoot} / UUID {_snapshot.Identity.CalibreLibraryUuid}";
    public string SourceSummary => _source?.Journal is null ? "Source execution not inspected."
        : $"Execution {_source.Journal.ExecutionId}; state {_source.Journal.LastState}; "
          + $"original manifest {_source.OriginalBackupManifest?.ManifestDigest}";
    public string OriginalBackupStatus => _source?.IsVerified == true
        ? "Original Milestone 7 backup: independently verified."
        : "Original Milestone 7 backup: not verified.";
    public string CurrentBackupStatus => _preparation is null
        ? "Current-state backup: required before mutation."
        : $"Current-state backup destination verified: {_preparation.CanonicalBackupDestinationIdentity}";
    public string PlanSummary => _plan is null ? "No recovery plan generated."
        : $"Plan {_plan.Id}; revision {_plan.ArtifactRevision.Value}; state {_plan.State}; SHA-256 {_plan.ContentDigest}";
    public string CapabilitySummary => _profile is null ? "Recovery capability profile unavailable."
        : $"{_profile.ProfileIdentity}: {_profile.Capabilities.Count(value => value.IsDispatchable)} "
          + $"of {_profile.Capabilities.Count} capabilities enabled.";
    public string CancelActionText => MutationStarted
        ? "_Stop at next verified safe boundary" : "_Cancel before mutation";
    public bool IsMutationInFlight => IsBusy && MutationStarted;
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
    public bool OtherMutatorsClosed
    {
        get => _otherMutatorsClosed;
        set { if (SetProperty(ref _otherMutatorsClosed, value)) ExecuteCommand.NotifyCanExecuteChanged(); }
    }
    public bool SafeBoundaryUnderstood
    {
        get => _safeBoundaryUnderstood;
        set { if (SetProperty(ref _safeBoundaryUnderstood, value)) ExecuteCommand.NotifyCanExecuteChanged(); }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsMutationInFlight));
                NotifyCommands();
            }
        }
    }
    public IReadOnlyList<RecoveryIssueRowViewModel> Issues
    {
        get => _issues;
        private set => SetProperty(ref _issues, value);
    }
    public IReadOnlyList<RecoveryReconciliationRowViewModel> ReconciliationRecords
    {
        get => _records;
        private set => SetProperty(ref _records, value);
    }
    public IReadOnlyList<RecoveryOperationRowViewModel> Operations
    {
        get => _operations;
        private set => SetProperty(ref _operations, value);
    }
    public IReadOnlyList<RecoveryPreservationRowViewModel> PreservedContent
    {
        get => _preserved;
        private set => SetProperty(ref _preserved, value);
    }
    public IReadOnlyList<RecoveryRecordMappingRowViewModel> RecordIdMappings
    {
        get => _mappings;
        private set => SetProperty(ref _mappings, value);
    }

    public void UpdateSnapshot(LibrarySnapshot? snapshot)
    {
        if (_snapshot?.Identity != snapshot?.Identity)
        {
            _source = null;
            _plan = null;
            _preparation = null;
            ResetPresentation();
            Status = "Library changed; select and reverify one Milestone 7 execution.";
        }
        _snapshot = snapshot;
        OnPropertyChanged(nameof(LibrarySummary));
        NotifyCommands();
    }

    private async Task SelectSourceAsync()
    {
        string? selected = _sourcePicker.PickSourceExecutionFolder(
            null);
        if (selected is null) return;
        IsBusy = true;
        try
        {
            SourceBundle = selected;
            _source = await _inspect.ExecuteAsync(new(selected), CancellationToken.None);
            Issues = _source.Issues.Select(RecoveryIssueRowViewModel.From).ToArray();
            Status = _source.IsVerified
                ? "Original plan, journal, manifest, and backup entries verified. Reconcile current state next."
                : "Source execution is not eligible; review blocking issues.";
            OnPropertyChanged(nameof(SourceSummary));
            OnPropertyChanged(nameof(OriginalBackupStatus));
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private async Task ReconcileAndPlanAsync()
    {
        IsBusy = true;
        try
        {
            _preparation = null;
            CalibreToolDiscoveryResult discovery = await _toolDiscovery.DiscoverAndProbeAsync(
                _snapshot!.Identity.LibraryRoot, CancellationToken.None);
            _tool = discovery.Tool;
            if (_tool is null && _source?.SourceToolIdentity is { } sourceTool)
                _tool = new(sourceTool.CanonicalExecutableIdentity, sourceTool, []);
            _profile = _tool is null ? null : _profiles.EvaluateRecoveryProfile(_tool);
            ReconcileCurrentRecoveryStateResult reconciled = await _reconcile.ExecuteAsync(
                new(_source!, _snapshot.Identity.LibraryRoot),
                new Progress<LibraryScanProgress>(value =>
                    ProgressMessage = value.Message), CancellationToken.None);
            _current = reconciled.CurrentState;
            _currentReconciliation = reconciled.Reconciliation;
            List<RecoveryIssue> allIssues = [.. reconciled.Issues];
            foreach (Domain.Executions.ExecutionIssue issue in discovery.Issues)
                allIssues.Add(new($"RECOVERY.{issue.Code}", RecoveryIssueSeverity.Blocking,
                    "Calibre capability", issue.Explanation));
            if (_current is not null && _currentReconciliation is not null && _profile is not null)
            {
                RecoveryEligibilityResult eligibility = _eligibility.Execute(new(
                    _source!, _current, _snapshot.Identity.LibraryRoot, _profile, false));
                allIssues.AddRange(eligibility.Issues);
                CurrentStateReconciliation planningReconciliation = CurrentStateReconciliation.Create(
                    _currentReconciliation.SourceExecutionIdentity,
                    _currentReconciliation.CurrentLibraryUuid,
                    _currentReconciliation.CurrentLibrarySchemaVersion,
                    _currentReconciliation.FullStateFingerprint,
                    _currentReconciliation.AffectedStateFingerprint,
                    _currentReconciliation.UnrelatedStateFingerprint,
                    _currentReconciliation.SourceOperations,
                    _currentReconciliation.Records,
                    allIssues);
                _plan = _generate.Execute(new(_source!, planningReconciliation,
                    _snapshot.Identity.LibraryRoot, _profile));
                PresentPlan();
                Status = _plan.State == RecoveryPlanState.Valid
                    ? "Immutable recovery plan generated. Review every difference and operation before approval."
                    : "Recovery plan is blocked. No recovery mutation can start.";
            }
            else
            {
                Issues = allIssues.Distinct().Select(RecoveryIssueRowViewModel.From).ToArray();
                Status = "Reconciliation or exact recovery capability discovery failed.";
            }
            OnPropertyChanged(nameof(CapabilitySummary));
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private void ApprovePlan()
    {
        foreach (string code in _plan!.Definition.RequiredWarningCodes)
        {
            RecoveryIssue warning = _plan.Definition.Issues.Single(value =>
                value.Code == code
                && value.Severity == RecoveryIssueSeverity.AcknowledgementRequired);
            if (!_confirmation.ConfirmWarningAcknowledgement(warning))
            {
                Status = $"Recovery plan was not approved because warning {code} was not acknowledged.";
                return;
            }
        }
        if (!_confirmation.ConfirmPlanApproval(_plan!)) return;
        _plan = _approve.Execute(_plan!, _plan!.Definition.RequiredWarningCodes);
        PresentPlan();
        Status = "Exact immutable recovery plan approved locally. Export and prepare it before execution.";
    }

    private async Task ExportPlanAsync()
    {
        string? path = _planPicker.PickNewRecoveryPlanPath(
            string.IsNullOrWhiteSpace(BackupDestination) ? null : BackupDestination);
        if (path is null) return;
        RecoveryPlanStoreResult result = await _export.ExportPlanAsync(
            _plan!, path, _snapshot!.Identity.LibraryRoot, CancellationToken.None);
        ArtifactStatus = result.IsSuccess
            ? $"Immutable recovery plan exported create-new: {result.ArtifactIdentity}"
            : string.Join(" ", result.Issues.Select(value => value.Explanation));
    }

    private void ChooseBackup()
    {
        string? selected = _backupPicker.PickBackupFolder(
            string.IsNullOrWhiteSpace(BackupDestination) ? null : BackupDestination);
        if (selected is null) return;
        BackupDestination = selected;
        _preparation = null;
        OnPropertyChanged(nameof(CurrentBackupStatus));
        NotifyCommands();
    }

    private async Task PrepareAsync()
    {
        IsBusy = true;
        try
        {
            _preparation = await _prepare.ExecuteAsync(new(_plan!, _source!,
                    _snapshot!.Identity.LibraryRoot, BackupDestination, _tool!, _profile!),
                new Progress<LibraryScanProgress>(value => ProgressMessage = value.Message),
                CancellationToken.None);
            Issues = _preparation.Issues.Select(RecoveryIssueRowViewModel.From).ToArray();
            Status = _preparation.IsReady
                ? "Preflight passed. The source and approved current reconciliation remain exact."
                : "Recovery preflight is blocked; no mutation is permitted.";
            OnPropertyChanged(nameof(CurrentBackupStatus));
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private async Task ExecuteAsync()
    {
        if (!_confirmation.ConfirmRecoveryExecution(
                _plan!, _snapshot!.Identity.LibraryRoot, BackupDestination)) return;
        _cancellation = new();
        IsBusy = true;
        MutationStarted = false;
        try
        {
            RecoveryExecutionConfirmation confirmation = new(_plan!.Id, _plan.ArtifactRevision,
                _plan.ContentDigest, _plan.Definition.InputIdentity.SourceExecutionId,
                _plan.Definition.InputIdentity.CurrentLibraryUuid,
                _plan.Definition.InputIdentity.CanonicalRootIdentityDigest,
                _plan.Definition.InputIdentity.FullStateFingerprint,
                _plan.Definition.InputIdentity.RecoveryCapabilityProfile,
                _preparation!.CanonicalBackupDestinationIdentity!,
                ExecuteApprovedRecoveryPlanUseCase.ComputeDestructiveDigest(
                    _plan.Definition.OperationGraph),
                _clock.GetUtcNow(), OtherMutatorsClosed, SafeBoundaryUnderstood);
            Progress<RecoveryProgress> progress = new(value =>
            {
                ProgressMessage = value.Message;
                MutationStarted = value.MutationStarted;
                ProgressPercent = value.TotalOperations == 0 ? 0
                    : value.CompletedOperations * 100d / value.TotalOperations;
            });
            RecoveryExecutionResult result = await _execute.ExecuteAsync(new(
                _plan, _source!, _snapshot.Identity.LibraryRoot, BackupDestination,
                _tool!, _profile!, confirmation,
                typeof(RecoveryWorkspaceViewModel).Assembly.GetName().Version?.ToString() ?? "unknown"),
                progress, _cancellation.Token);
            Issues = result.Issues.Select(RecoveryIssueRowViewModel.From).ToArray();
            RecordIdMappings = result.RecordIdMappings.Select(value =>
                new RecoveryRecordMappingRowViewModel(value.LogicalRecordId.Value,
                    value.OriginalRecordId.Value,
                    value.CleanupTargetRecordId?.Value.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    value.RecoveredRecordId.Value,
                    string.Join(", ", value.RestoredFormats),
                    string.Join(", ", value.Identifiers))).ToArray();
            ResultSummary = $"{DisplayState(result.State)}. "
                + $"Mutation started: {result.MutationStarted}; destructive phase: {result.DestructiveRecoveryStarted}; "
                + $"semantic pre-state restored: {result.SemanticPreStateRestored}; "
                + $"unexpected data preserved: {result.PreservedUnexpectedContent}.";
            ArtifactStatus = $"Recovery bundle: {result.BundleIdentity}; journal: {result.JournalIdentity}; "
                + $"current-state manifest: {result.CurrentStateBackupManifestDigest}.";
            Status = result.IsRecovered
                ? "Recovery completed and final semantic verification passed."
                : "Recovery did not verify as fully recovered. Preserve all artifacts and follow the reported state.";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
            NotifyCommands();
        }
    }

    private void RequestStop()
    {
        _cancellation?.Cancel();
        Status = MutationStarted
            ? "Safe-stop requested. The active Calibre process will not be terminated; stopping occurs after verification."
            : "Cancellation requested before recovery mutation.";
    }

    private void PresentPlan()
    {
        ReconciliationRecords = _plan!.Definition.Reconciliation.Records.Select(value =>
            new RecoveryReconciliationRowViewModel(value.Identity.LogicalRecordId.Value,
                value.Identity.OriginalRecordId.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                value.Identity.CurrentRecordId?.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ?? "missing",
                string.Join(", ", value.Classifications),
                string.Join(", ", value.Formats.Select(format =>
                    $"{format.Format}: {format.Classification}")))).ToArray();
        Operations = _plan.Definition.OperationGraph.Operations.Select((value, index) =>
            new RecoveryOperationRowViewModel((index + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                value.Phase.ToString(), value.Kind.ToString(), value.LogicalRecordId.Value,
                value.CurrentRecordId?.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture) ?? "new record",
                value.Format ?? string.Empty,
                string.Join(", ", value.DependencyIds.Select(id => id.Value)),
                value.Risk.ToString(), value.RequiredCapability, value.Reason)).ToArray();
        PreservedContent = _plan.Definition.ExpectedFinalState.PreservedContent.Select(value =>
            new RecoveryPreservationRowViewModel(value.LogicalRecordId.Value,
                value.CurrentRecordId.Value, value.Format, value.Fingerprint.Sha256.Value,
                value.Fingerprint.SizeInBytes, value.PreservationReason)).ToArray();
        Issues = _plan.Definition.Issues.Select(RecoveryIssueRowViewModel.From).ToArray();
        OnPropertyChanged(nameof(PlanSummary));
        NotifyCommands();
    }

    private bool CanReconcile() => !IsBusy && _snapshot is not null && _source?.IsVerified == true;
    private bool CanApprove() => !IsBusy && _plan?.State == RecoveryPlanState.Valid;
    private bool CanExport() => !IsBusy && _plan is not null;
    private bool CanPrepare() => !IsBusy && _plan?.State == RecoveryPlanState.Approved
        && _source is not null && _snapshot is not null && _tool is not null
        && _profile is not null && !string.IsNullOrWhiteSpace(BackupDestination);
    private bool CanExecute() => !IsBusy && _preparation?.IsReady == true
        && OtherMutatorsClosed && SafeBoundaryUnderstood;

    private void NotifyCommands()
    {
        SelectSourceCommand.NotifyCanExecuteChanged();
        ReconcileAndPlanCommand.NotifyCanExecuteChanged();
        ApprovePlanCommand.NotifyCanExecuteChanged();
        ExportPlanCommand.NotifyCanExecuteChanged();
        ChooseBackupCommand.NotifyCanExecuteChanged();
        PrepareCommand.NotifyCanExecuteChanged();
        ExecuteCommand.NotifyCanExecuteChanged();
        CancelOrSafeStopCommand.NotifyCanExecuteChanged();
    }

    private void ResetPresentation()
    {
        Issues = [];
        ReconciliationRecords = [];
        Operations = [];
        PreservedContent = [];
        RecordIdMappings = [];
        ResultSummary = string.Empty;
        ArtifactStatus = string.Empty;
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(OriginalBackupStatus));
        OnPropertyChanged(nameof(CurrentBackupStatus));
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(CapabilitySummary));
    }

    private static string DisplayState(RecoveryExecutionState state) => state switch
    {
        RecoveryExecutionState.Recovered => "Recovered",
        RecoveryExecutionState.PartiallyRecovered => "Partially Recovered",
        RecoveryExecutionState.VerificationFailed => "Verification Failed",
        RecoveryExecutionState.ManualInterventionRequired => "Manual Intervention Required",
        _ => state.ToString(),
    };

    public void Dispose()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
    }
}
