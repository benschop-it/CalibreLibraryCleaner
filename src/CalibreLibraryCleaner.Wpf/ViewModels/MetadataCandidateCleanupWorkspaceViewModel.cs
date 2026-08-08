using System.ComponentModel;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class MetadataCandidateCleanupWorkspaceViewModel : ObservableObject
{
    private readonly ExecuteBulkMetadataCandidateCleanupUseCase _execute;
    private readonly IMetadataCandidateCleanupConfirmationService _confirmation;
    private LibrarySnapshot? _snapshot;
    private IReadOnlyList<MetadataDuplicateGroupRowViewModel> _groups = [];
    private string _status = "Run or load a scan to find metadata candidates.";
    private string _progressMessage = string.Empty;
    private string _resultSummary = string.Empty;
    private double _progressPercentage;
    private bool _isBusy;

    public MetadataCandidateCleanupWorkspaceViewModel(
        ExecuteBulkMetadataCandidateCleanupUseCase execute,
        IMetadataCandidateCleanupConfirmationService confirmation)
    {
        _execute = execute;
        _confirmation = confirmation;
        ProcessCandidatesCommand = new AsyncRelayCommand(ProcessCandidatesAsync, CanProcessCandidates);
    }

    public IAsyncRelayCommand ProcessCandidatesCommand { get; }

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
            if (SetProperty(ref _isBusy, value)) ProcessCandidatesCommand.NotifyCanExecuteChanged();
        }
    }

    public void UpdateContext(
        LibrarySnapshot? snapshot,
        IReadOnlyList<MetadataDuplicateGroupRowViewModel> groups)
    {
        foreach (MetadataDuplicateGroupRowViewModel group in _groups)
            group.PropertyChanged -= OnGroupPropertyChanged;
        _snapshot = snapshot;
        _groups = groups;
        foreach (MetadataDuplicateGroupRowViewModel group in _groups)
            group.PropertyChanged += OnGroupPropertyChanged;
        int eligible = groups.Count(value => !value.Skip && value.KeeperMember is not null);
        int skipped = groups.Count - eligible;
        Status = snapshot is null
            ? "Run or load an authoritative scan before processing metadata candidates."
            : groups.Count == 0
                ? "No metadata candidate groups are available."
                : $"{eligible:N0} group(s) will be processed; {skipped:N0} group(s) are skipped.";
        ProcessCandidatesCommand.NotifyCanExecuteChanged();
    }

    private bool CanProcessCandidates() => !IsBusy && _snapshot is not null
        && _groups.Any(value => !value.Skip && value.KeeperMember is not null);

    private async Task ProcessCandidatesAsync()
    {
        if (_snapshot is null) return;
        MetadataCandidateCleanupSelection[] selections = _groups.Select(group => new MetadataCandidateCleanupSelection(
            group.GroupId,
            group.KeeperMember is null ? null : new CalibreBookId(group.KeeperMember.BookId),
            group.Skip)).ToArray();
        int eligible = selections.Count(value => !value.Skip && value.KeeperBookId is not null);
        if (!_confirmation.ConfirmExternalBackup(eligible))
        {
            Status = "Metadata candidate cleanup canceled. Confirm a complete external backup before retrying.";
            return;
        }

        IsBusy = true;
        ProgressPercentage = 0;
        ProgressMessage = string.Empty;
        ResultSummary = string.Empty;
        try
        {
            Progress<BulkMetadataCandidateCleanupProgress> progress = new(value =>
            {
                ProgressMessage = value.Message;
                ProgressPercentage = value.TotalOperations == 0 ? 0
                    : 100d * value.CompletedOperations / value.TotalOperations;
            });
            BulkMetadataCandidateCleanupResult result = await _execute.ExecuteAsync(new(
                _snapshot.Identity.LibraryRoot, selections, ExternalBackupConfirmed: true),
                progress, CancellationToken.None).ConfigureAwait(true);
            ResultSummary = $"Transferred {result.TransferredFormatCount:N0} complementary format(s), removed "
                + $"{result.RemovedFormatCount:N0} source format(s), and deleted {result.RemovedRecordCount:N0} source record(s)."
                + (result.SkippedGroupCount > 0 ? $" {result.SkippedGroupCount:N0} group(s) were skipped." : string.Empty);
            Status = result.IsCompleted
                ? "Metadata candidate cleanup completed."
                : string.Join(" ", result.Issues.Select(value => $"{value.Code}: {value.Explanation}"));
        }
        finally
        {
            IsBusy = false;
            ProcessCandidatesCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(MetadataDuplicateGroupRowViewModel.Skip)
            or nameof(MetadataDuplicateGroupRowViewModel.KeeperMember))
        {
            int eligible = _groups.Count(value => !value.Skip && value.KeeperMember is not null);
            Status = $"{eligible:N0} group(s) will be processed; {_groups.Count - eligible:N0} group(s) are skipped.";
            ProcessCandidatesCommand.NotifyCanExecuteChanged();
        }
    }
}
