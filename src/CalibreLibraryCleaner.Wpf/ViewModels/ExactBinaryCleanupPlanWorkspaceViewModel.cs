using System.ComponentModel;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class ExactBinaryCleanupPlanWorkspaceViewModel : ObservableObject
{
    private readonly ExecuteBulkExactDuplicateCleanupUseCase _execute;
    private readonly IExactDuplicateCleanupConfirmationService _confirmation;
    private LibrarySnapshot? _snapshot;
    private IReadOnlyList<ExactDuplicateGroupRowViewModel> _groups = [];
    private string _status = "Run a scan to find exact file duplicates.";
    private string _progressMessage = string.Empty;
    private string _resultSummary = string.Empty;
    private double _progressPercentage;
    private bool _isBusy;

    public ExactBinaryCleanupPlanWorkspaceViewModel(
        ExecuteBulkExactDuplicateCleanupUseCase execute,
        IExactDuplicateCleanupConfirmationService confirmation)
    {
        _execute = execute;
        _confirmation = confirmation;
        RemoveDuplicatesCommand = new AsyncRelayCommand(RemoveDuplicatesAsync, CanRemoveDuplicates);
    }

    public IAsyncRelayCommand RemoveDuplicatesCommand { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
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
        _groups = groups;
        foreach (ExactDuplicateGroupRowViewModel group in groups)
            group.PropertyChanged += OnGroupPropertyChanged;
        int eligible = groups.Count(value => !value.Skip && value.IsCleanupEligible && value.RetainedMember is not null);
        Status = snapshot is null
            ? "Run or load an authoritative scan before removing duplicates."
            : eligible == 0
                ? "No removable exact duplicate groups are available."
                : $"{eligible:N0} exact duplicate group(s) are ready. Select another row to change any keeper, then remove duplicates.";
        RemoveDuplicatesCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemoveDuplicates() => !IsBusy && _snapshot is not null
        && _groups.Any(value => !value.Skip && value.IsCleanupEligible && value.RetainedMember is not null);

    private async Task RemoveDuplicatesAsync()
    {
        if (_snapshot is null) return;
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
            Status = result.IsCompleted
                ? "Duplicate cleanup completed."
                : string.Join(" ", result.Issues.Select(value => $"{value.Code}: {value.Explanation}"));
        }
        finally
        {
            IsBusy = false;
            RemoveDuplicatesCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ExactDuplicateGroupRowViewModel.Skip)
            or nameof(ExactDuplicateGroupRowViewModel.RetainedMember))
            UpdateContext(_snapshot, _groups);
    }
}
