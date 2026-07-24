using System.Collections.ObjectModel;
using System.Globalization;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class CleanupPlanWorkspaceViewModel : ObservableObject
{
    public event Action<CleanupPlan?, bool>? SelectedPlanChanged;
    private readonly GenerateCleanupPlanUseCase _generate;
    private readonly ValidateCleanupPlanUseCase _validate;
    private readonly ApproveCleanupPlanUseCase _approve;
    private readonly RevokeCleanupPlanUseCase _revoke;
    private readonly ExportCleanupPlanUseCase _export;
    private readonly ImportCleanupPlanUseCase _import;
    private readonly ICleanupPlanFilePicker _filePicker;
    private readonly ICleanupPlanConfirmationService _confirmation;
    private readonly ObservableCollection<CleanupPlanSummaryRowViewModel> _plans = [];
    private LibrarySnapshot? _snapshot;
    private ReviewedConsolidationRecommendation? _reviewed;
    private CleanupPlanSummaryRowViewModel? _selectedPlan;
    private IReadOnlyList<CleanupPlanIssueRowViewModel> _generationIssues = [];
    private CleanupPlanValidationResult? _selectedValidation;
    private string _revocationReason = string.Empty;
    private string _status = "Generate a plan from a current reviewed recommendation or import an external plan.";

    public CleanupPlanWorkspaceViewModel(
        GenerateCleanupPlanUseCase generate,
        ValidateCleanupPlanUseCase validate,
        ApproveCleanupPlanUseCase approve,
        RevokeCleanupPlanUseCase revoke,
        ExportCleanupPlanUseCase export,
        ImportCleanupPlanUseCase import,
        ICleanupPlanFilePicker filePicker,
        ICleanupPlanConfirmationService confirmation)
    {
        _generate = generate;
        _validate = validate;
        _approve = approve;
        _revoke = revoke;
        _export = export;
        _import = import;
        _filePicker = filePicker;
        _confirmation = confirmation;
        Plans = new ReadOnlyObservableCollection<CleanupPlanSummaryRowViewModel>(_plans);
        GenerateCommand = new RelayCommand(Generate, () => _snapshot is not null && _reviewed is not null);
        ValidateCommand = new RelayCommand(Validate, CanUseCurrentPlan);
        ApproveCommand = new RelayCommand(Approve, () => CanUseCurrentPlan()
            && CurrentPlan is { State: CleanupPlanState.Valid } plan && plan.Validation.IsValid
            && _reviewed?.Generated.GroupId == plan.Definition.Provenance.GroupId);
        RevokeCommand = new RelayCommand(Revoke, () => CurrentPlan is { State: CleanupPlanState.Approved } && !string.IsNullOrWhiteSpace(RevocationReason));
        ExportCommand = new AsyncRelayCommand(ExportAsync, CanUseCurrentPlan);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => _snapshot is not null);
    }

    public ReadOnlyObservableCollection<CleanupPlanSummaryRowViewModel> Plans { get; }
    public IRelayCommand GenerateCommand { get; }
    public IRelayCommand ValidateCommand { get; }
    public IRelayCommand ApproveCommand { get; }
    public IRelayCommand RevokeCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }

    public CleanupPlanSummaryRowViewModel? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (SetProperty(ref _selectedPlan, value))
            {
                _selectedValidation = value?.Plan.Validation;
                RefreshDetails();
                NotifyCommands();
                SelectedPlanChanged?.Invoke(value?.Plan, value?.IsImported ?? false);
            }
        }
    }

    public string RevocationReason
    {
        get => _revocationReason;
        set
        {
            if (SetProperty(ref _revocationReason, value ?? string.Empty)) RevokeCommand.NotifyCanExecuteChanged();
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public IReadOnlyList<CleanupPlanIssueRowViewModel> GenerationIssues { get => _generationIssues; private set => SetProperty(ref _generationIssues, value); }
    public string PlanIdentity => CurrentPlan is null ? string.Empty : $"{CurrentPlan.Id} / revision {CurrentPlan.ArtifactRevision.Value} / {CurrentPlan.State}";
    public string PlanTimes => CurrentPlan is null ? string.Empty : $"Created {CurrentPlan.CreatedAtUtc:u}; last locally checked {(_selectedValidation ?? CurrentPlan.Validation).ValidatedAtUtc:u}";
    public string ContentDigest => CurrentPlan?.ContentDigest.Value ?? string.Empty;
    public string SourceIdentity => CurrentPlan is null ? string.Empty : $"{CurrentPlan.InputIdentity.LibraryUuid} / schema {CurrentPlan.InputIdentity.SchemaVersion}";
    public string GroupAndMembers => CurrentPlan is null ? string.Empty : $"{CurrentPlan.InputIdentity.GroupId.Value}; records {string.Join(", ", CurrentPlan.Definition.InvolvedRecordIds.Select(id => id.Value))}";
    public string TargetAndMetadata => CurrentPlan is null ? string.Empty : $"Target {CurrentPlan.Definition.TargetRecordId.Value}; metadata source {CurrentPlan.Definition.MetadataRetention.SourceRecordId.Value}";
    public string ProvenanceSummary => CurrentPlan is null ? string.Empty : $"{CurrentPlan.Definition.Provenance.RecommendationModelVersion}; {CurrentPlan.Definition.Provenance.RecommendationInputVersion}; review {CurrentPlan.Definition.Provenance.ReviewStatus}; policy {CurrentPlan.PolicyVersion}";
    public string OverrideSummary => CurrentPlan is null ? string.Empty : $"Override {CurrentPlan.Definition.Provenance.UserOverride.RequestedStatus} at {CurrentPlan.Definition.Provenance.UserOverride.ReviewedAtUtc:u}; metadata change {CurrentPlan.Definition.Provenance.UserOverride.MetadataSourceRecordId?.Value.ToString(CultureInfo.InvariantCulture) ?? "none"}; format actions {string.Join(", ", CurrentPlan.Definition.Provenance.UserOverride.FormatActions)}";
    public string ApprovalSummary => CurrentPlan?.Approval is null ? "Not approved"
        : $"{(SelectedPlan?.IsImported == true ? "Imported approval (informational only). " : string.Empty)}Approved {CurrentPlan.Approval.ApprovedAtUtc:u}, revision {CurrentPlan.Approval.ApprovedRevision.Value}, digest {CurrentPlan.Approval.ContentDigest}";
    public string RevocationSummary => CurrentPlan?.Revocation is null ? string.Empty : $"Revoked {CurrentPlan.Revocation.RevokedAtUtc:u}: {CurrentPlan.Revocation.Reason}";
    public IReadOnlyList<CleanupPlanFormatRetentionRowViewModel> FormatRetentions => CurrentPlan?.Definition.FormatRetentions.Select(value => new CleanupPlanFormatRetentionRowViewModel(
        value.Format, value.SourceState.RecordId.Value, value.TargetRecordId.Value, value.SourceState.RelativePath,
        value.SourceState.Fingerprint.SizeInBytes, value.SourceState.Fingerprint.Sha256.Value, value.Mode.ToString(), string.Join("; ", value.Preconditions))).ToArray() ?? [];
    public IReadOnlyList<CleanupPlanFormatRemovalRowViewModel> FormatRemovals => CurrentPlan?.Definition.FormatRemovals.Select(value => new CleanupPlanFormatRemovalRowViewModel(
        value.RecordId.Value, value.Format, value.RelativePath, value.Reason.ToString(), value.RetainedFormatInstructionId, value.BackupRequirementId, value.BytesIdenticalToRetainedSource)).ToArray() ?? [];
    public IReadOnlyList<CleanupPlanRecordRemovalRowViewModel> RecordRemovals => CurrentPlan?.Definition.RecordRemovals.Select(value => new CleanupPlanRecordRemovalRowViewModel(
        value.RecordId.Value, string.Join(", ", value.BackupRequirementIds), string.Join(", ", value.RequiredRetainedFormatInstructionIds), string.Join("; ", value.Preconditions))).ToArray() ?? [];
    public IReadOnlyList<CleanupPlanExpectedRecordRowViewModel> ExpectedRecords => CurrentPlan?.Definition.ExpectedLibraryState.Records.Select(value => new CleanupPlanExpectedRecordRowViewModel(
        value.RecordId.Value, value.Title, string.Join("; ", value.Authors.Select(author => $"{author.Name} [{author.Id.Value}]")),
        string.Join("; ", value.Identifiers.Select(id => $"{id.Type}={id.Value}")),
        $"{value.Publisher}; {value.PublicationDate:u}; {value.Series}/{value.SeriesIndex}", string.Join(", ", value.Languages),
        value.HasCover ? "Reported; backup/validation required" : "Not reported", value.RelativeDirectory)).ToArray() ?? [];
    public IReadOnlyList<CleanupPlanExpectedFormatRowViewModel> ExpectedFormats => CurrentPlan?.Definition.ExpectedLibraryState.Records.SelectMany(record => record.Formats).Select(value => new CleanupPlanExpectedFormatRowViewModel(
        value.RecordId.Value, value.Format, value.StoredFileName, value.RelativePath, value.Status.ToString(), value.Fingerprint.SizeInBytes,
        value.Fingerprint.Sha256.Value, value.Observation.CreationTimeUtc, value.Observation.LastWriteTimeUtc, value.Observation.Attributes,
        value.ObservationSourceVersion)).ToArray() ?? [];
    public IReadOnlyList<CleanupPlanBackupRowViewModel> BackupRequirements => CurrentPlan?.Definition.BackupRequirements.Select(value => new CleanupPlanBackupRowViewModel(
        value.Id, value.Kind.ToString(), value.RecordId?.Value, value.Format, "Required; not created", value.Explanation)).ToArray() ?? [];
    public IReadOnlyList<CleanupPlanIssueRowViewModel> Issues => CurrentPlan?.Validation.Issues.Select(Issue).ToArray() ?? [];
    public IReadOnlyList<CleanupPlanLifecycleRowViewModel> LifecycleHistory => CurrentPlan?.LifecycleHistory.Select(value => new CleanupPlanLifecycleRowViewModel(
        value.Revision.Value, $"{value.FromState} -> {value.ToState}", value.ChangedAtUtc, value.Reason)).ToArray() ?? [];

    private CleanupPlan? CurrentPlan => SelectedPlan?.Plan;

    public void UpdateContext(LibrarySnapshot? snapshot, ReviewedConsolidationRecommendation? reviewed)
    {
        if (_snapshot is not null && snapshot is not null
            && !string.Equals(_snapshot.Identity.LibraryRoot, snapshot.Identity.LibraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            _plans.Clear();
            SelectedPlan = null;
        }
        _snapshot = snapshot;
        _reviewed = reviewed;
        if (snapshot is not null && reviewed is not null)
        {
            CleanupPlan[] latest = _plans.Where(value => value.Plan.InputIdentity.LibraryUuid == snapshot.Identity.CalibreLibraryUuid
                    && value.Plan.Definition.Provenance.GroupId == reviewed.Generated.GroupId)
                .GroupBy(value => value.Plan.Id)
                .Select(group => group.OrderByDescending(value => value.Revision).First().Plan)
                .ToArray();
            foreach (CleanupPlan plan in latest)
            {
                CleanupPlanOperationOutcome outcome = _validate.Execute(plan, snapshot, reviewed);
                if (outcome.Plan is not null && outcome.Plan.ArtifactRevision != plan.ArtifactRevision)
                    AddRevision(outcome.Plan);
            }
        }
        NotifyCommands();
    }

    public void ReconcileAfterSuccessfulScan(LibrarySnapshot snapshot)
    {
        if (_snapshot is not null
            && !string.Equals(_snapshot.Identity.LibraryRoot, snapshot.Identity.LibraryRoot, StringComparison.OrdinalIgnoreCase))
        {
            _plans.Clear();
            SelectedPlan = null;
        }
        _snapshot = snapshot;
        CleanupPlan[] latest = _plans.Where(value => value.Plan.InputIdentity.LibraryUuid == snapshot.Identity.CalibreLibraryUuid)
            .GroupBy(value => value.Plan.Id).Select(group => group.OrderByDescending(value => value.Revision).First().Plan).ToArray();
        foreach (CleanupPlan plan in latest)
        {
            CleanupPlanOperationOutcome outcome = _validate.Execute(plan, snapshot);
            if (outcome.Plan is not null && outcome.Plan.ArtifactRevision != plan.ArtifactRevision) AddRevision(outcome.Plan);
        }
        NotifyCommands();
    }

    private void Generate()
    {
        if (_snapshot is null || _reviewed is null) return;
        CleanupPlanGenerationOutcome outcome = _generate.Execute(_snapshot, _reviewed);
        GenerationIssues = outcome.Validation.Issues.Select(Issue).ToArray();
        if (outcome.Plan is null)
        {
            Status = "No cleanup plan was generated. Review the blocking issues.";
            return;
        }
        if (!AddRevision(outcome.Plan)) return;
        Status = "Valid descriptive cleanup plan generated in memory. No Calibre change or backup occurred.";
    }

    private void Validate()
    {
        if (CurrentPlan is null || _snapshot is null) return;
        CleanupPlanOperationOutcome outcome = _validate.Execute(CurrentPlan, _snapshot,
            _reviewed?.Generated.GroupId == CurrentPlan.Definition.Provenance.GroupId ? _reviewed : null);
        _selectedValidation = outcome.Validation;
        if (outcome.Plan is not null && outcome.Plan.ArtifactRevision != CurrentPlan.ArtifactRevision) AddRevision(outcome.Plan);
        else RefreshDetails();
        Status = outcome.Plan?.State == CleanupPlanState.Stale ? "The plan is stale and approval is invalidated." : "Plan validation completed against the current snapshot.";
    }

    private void Approve()
    {
        if (CurrentPlan is null || _snapshot is null || !_confirmation.ConfirmApproval(CurrentPlan)) return;
        CleanupPlanOperationOutcome outcome = _approve.Execute(CurrentPlan, _snapshot,
            _reviewed?.Generated.GroupId == CurrentPlan.Definition.Provenance.GroupId ? _reviewed : null);
        if (outcome.Plan is not null) AddRevision(outcome.Plan);
        Status = outcome.IsSuccess ? "Plan body approved explicitly. No Calibre change or backup occurred." : "Approval was blocked.";
    }

    private void Revoke()
    {
        if (CurrentPlan is null || string.IsNullOrWhiteSpace(RevocationReason)
            || !_confirmation.ConfirmRevocation(CurrentPlan, RevocationReason)) return;
        CleanupPlanOperationOutcome outcome = _revoke.Execute(CurrentPlan, RevocationReason);
        if (outcome.Plan is not null) AddRevision(outcome.Plan);
        Status = outcome.IsSuccess ? "Approval revoked; audit information was preserved." : "Revocation was not allowed.";
    }

    private async Task ExportAsync()
    {
        CleanupPlan? plan = CurrentPlan;
        LibrarySnapshot? snapshot = _snapshot;
        if (plan is null || snapshot is null
            || plan.InputIdentity.LibraryUuid != snapshot.Identity.CalibreLibraryUuid) return;
        string? path = _filePicker.PickExportDestination();
        if (path is null) return;
        CleanupPlanStoreWriteResult result = await _export.ExecuteAsync(plan, snapshot.Identity.LibraryRoot, path, CancellationToken.None);
        Status = result.IsSuccess ? "External cleanup-plan artifact exported." : result.Error!.Message;
    }

    private async Task ImportAsync()
    {
        if (_snapshot is null) return;
        string? path = _filePicker.PickImportSource();
        if (path is null) return;
        CleanupPlanOperationOutcome outcome = await _import.ExecuteAsync(_snapshot, path, CancellationToken.None);
        if (outcome.Plan is not null && !AddRevision(outcome.Plan, isImported: true))
        {
            GenerationIssues =
            [
                Issue(new("PLAN.ID_LINEAGE_CONFLICT", CleanupPlanIssueSeverity.BlockingError,
                    CleanupPlanIssueSubjectKind.Plan,
                    "The imported plan ID belongs to a different immutable body or lifecycle lineage.")),
            ];
            return;
        }
        _selectedValidation = outcome.Validation;
        GenerationIssues = outcome.Issues.Select(Issue).ToArray();
        Status = outcome.Plan is null ? "Cleanup-plan import was rejected." : $"Cleanup plan imported as {outcome.Plan.State}; imported approval is descriptive only.";
    }

    private bool AddRevision(CleanupPlan plan, bool isImported = false)
    {
        CleanupPlanSummaryRowViewModel[] lineage = _plans.Where(value => value.Plan.Id == plan.Id).ToArray();
        if (lineage.Any(value => !CleanupPlanLineagePolicy.IsCompatible(value.Plan, plan)))
        {
            Status = "Cleanup plan rejected because its plan ID belongs to a different immutable body or lifecycle lineage.";
            return false;
        }
        CleanupPlanSummaryRowViewModel? duplicate = lineage.SingleOrDefault(value => value.Revision == plan.ArtifactRevision.Value);
        if (duplicate is not null)
        {
            SelectedPlan = duplicate;
            return true;
        }
        isImported |= lineage.Any(value => value.IsImported);
        CleanupPlanSummaryRowViewModel row = new(plan, plan.Id.ToString(), plan.ArtifactRevision.Value, plan.State.ToString(),
            plan.InputIdentity.GroupId.Value, plan.Definition.TargetRecordId.Value, plan.ContentDigest.Value,
            plan.Approval is null ? "Not approved" : isImported ? "Imported approval (informational)" : plan.State.ToString(),
            isImported);
        _plans.Add(row);
        SelectedPlan = row;
        _selectedValidation = plan.Validation;
        return true;
    }

    private void RefreshDetails()
    {
        foreach (string property in new[]
        {
            nameof(PlanIdentity), nameof(PlanTimes), nameof(ContentDigest), nameof(SourceIdentity), nameof(GroupAndMembers),
            nameof(TargetAndMetadata), nameof(ProvenanceSummary), nameof(OverrideSummary), nameof(ApprovalSummary), nameof(RevocationSummary),
            nameof(FormatRetentions), nameof(FormatRemovals), nameof(RecordRemovals), nameof(ExpectedRecords),
            nameof(ExpectedFormats), nameof(BackupRequirements), nameof(Issues), nameof(LifecycleHistory),
        }) OnPropertyChanged(property);
    }

    private void NotifyCommands()
    {
        GenerateCommand.NotifyCanExecuteChanged();
        ValidateCommand.NotifyCanExecuteChanged();
        ApproveCommand.NotifyCanExecuteChanged();
        RevokeCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
    }

    private bool CanUseCurrentPlan() => CurrentPlan is not null && _snapshot is not null
        && CurrentPlan.InputIdentity.LibraryUuid == _snapshot.Identity.CalibreLibraryUuid;

    private static CleanupPlanIssueRowViewModel Issue(CleanupPlanIssue value) => new(
        value.Severity.ToString(), value.Code,
        $"{value.SubjectKind}{(value.RecordId is null ? string.Empty : $" / record {value.RecordId.Value.Value}")}{(value.Format is null ? string.Empty : $" / {value.Format}")}",
        value.Explanation);
}
