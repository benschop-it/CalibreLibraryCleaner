using System.ComponentModel;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class ExpandedCandidateCleanupWorkspaceViewModel : ObservableObject
{
    private readonly ExecuteBulkExpandedCandidateCleanupUseCase _execute;
    private readonly IExpandedCandidateCleanupConfirmationService _confirmation;
    private LibrarySnapshot? _snapshot;
    private IReadOnlyList<ExpandedCandidateGroupRowViewModel> _groups = [];
    private string _status = "Run a fresh scan to find content-confirmed expanded candidates.";
    private string _progressMessage = string.Empty;
    private string _resultSummary = string.Empty;
    private double _progressPercentage;
    private bool _isBusy;

    public ExpandedCandidateCleanupWorkspaceViewModel(
        ExecuteBulkExpandedCandidateCleanupUseCase execute,
        IExpandedCandidateCleanupConfirmationService confirmation)
    {
        _execute = execute;
        _confirmation = confirmation;
        ProcessCandidatesCommand = new AsyncRelayCommand(ProcessCandidatesAsync, CanProcessCandidates);
    }

    public IAsyncRelayCommand ProcessCandidatesCommand { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ProgressMessage { get => _progressMessage; private set => SetProperty(ref _progressMessage, value); }
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }
    public double ProgressPercentage { get => _progressPercentage; private set => SetProperty(ref _progressPercentage, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) ProcessCandidatesCommand.NotifyCanExecuteChanged();
        }
    }

    public void UpdateContext(
        LibrarySnapshot? snapshot,
        IReadOnlyList<ExpandedCandidateGroupRowViewModel> groups)
    {
        foreach (ExpandedCandidateGroupRowViewModel group in _groups)
            group.PropertyChanged -= OnGroupPropertyChanged;
        _snapshot = snapshot;
        _groups = groups;
        foreach (ExpandedCandidateGroupRowViewModel group in groups)
            group.PropertyChanged += OnGroupPropertyChanged;
        RefreshStatus();
    }

    private bool CanProcessCandidates() => !IsBusy && _snapshot is not null
        && _groups.Any(IsEligibleSelection);

    private async Task ProcessCandidatesAsync()
    {
        if (_snapshot is null) return;
        ExpandedCandidateCleanupSelection[] selections = _groups.Select(group => new ExpandedCandidateCleanupSelection(
            new(group.GroupId),
            group.KeeperMember is null ? null : new CalibreBookId(group.KeeperMember.BookId),
            group.Skip)).ToArray();
        int eligible = _groups.Count(IsEligibleSelection);
        if (!_confirmation.ConfirmExternalBackup(eligible))
        {
            Status = "Expanded candidate cleanup canceled. Confirm a complete external backup before retrying.";
            return;
        }

        IsBusy = true;
        ProgressPercentage = 0;
        ProgressMessage = string.Empty;
        ResultSummary = string.Empty;
        try
        {
            Progress<BulkExpandedCandidateCleanupProgress> progress = new(value =>
            {
                ProgressMessage = value.Message;
                ProgressPercentage = value.TotalOperations == 0 ? 0
                    : 100d * value.CompletedOperations / value.TotalOperations;
            });
            BulkExpandedCandidateCleanupResult result = await _execute.ExecuteAsync(new(
                _snapshot.Identity.LibraryRoot,
                selections,
                ExternalBackupConfirmed: true), progress, CancellationToken.None).ConfigureAwait(true);
            ResultSummary = $"Transferred {result.TransferredFormatCount:N0} complementary format(s), removed "
                + $"{result.RemovedFormatCount:N0} source format(s), and deleted {result.RemovedRecordCount:N0} source record(s)."
                + (result.SkippedGroupCount > 0 ? $" {result.SkippedGroupCount:N0} group(s) were skipped." : string.Empty);
            Status = result.IsCompleted
                ? "Expanded candidate cleanup completed. Run Scan to refresh inferred groups."
                : string.Join(" ", result.Issues.Select(value => $"{value.Code}: {value.Explanation}"));
        }
        finally
        {
            IsBusy = false;
            ProcessCandidatesCommand.NotifyCanExecuteChanged();
        }
    }

    private bool IsEligibleSelection(ExpandedCandidateGroupRowViewModel group) =>
        !group.Skip
        && group.KeeperMember is not null
        && string.Equals(group.Eligibility, "Cleanup eligible", StringComparison.Ordinal);

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ExpandedCandidateGroupRowViewModel.Skip)
            or nameof(ExpandedCandidateGroupRowViewModel.KeeperMember))
            RefreshStatus();
    }

    private void RefreshStatus()
    {
        int eligible = _groups.Count(IsEligibleSelection);
        int reviewOnly = _groups.Count(value =>
            !string.Equals(value.Eligibility, "Cleanup eligible", StringComparison.Ordinal));
        Status = _snapshot is null
            ? "Run or load authoritative state from a fresh scan before processing expanded candidates."
            : _groups.Count == 0
                ? "No expanded candidate groups are available."
                : $"{eligible:N0} group(s) will be processed; {_groups.Count - eligible:N0} group(s) are skipped or review-only"
                    + (reviewOnly > 0 ? $" ({reviewOnly:N0} require Rescan under the current policy)." : ".");
        ProcessCandidatesCommand.NotifyCanExecuteChanged();
    }
}
