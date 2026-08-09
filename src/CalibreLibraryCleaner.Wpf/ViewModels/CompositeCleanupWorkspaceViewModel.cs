using System.ComponentModel;
using System.Globalization;
using System.Text;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class CompositeCleanupWorkspaceViewModel : ObservableObject
{
    private readonly ExecuteCompositeCleanupUseCase _execute;
    private readonly ICompositeCleanupDialogService _dialog;
    private LibrarySnapshot? _snapshot;
    private IReadOnlyList<ExactDuplicateGroupRowViewModel> _exactGroups = [];
    private IReadOnlyList<MetadataDuplicateGroupRowViewModel> _metadataGroups = [];
    private IReadOnlyList<ExpandedCandidateGroupRowViewModel> _expandedGroups = [];
    private string _status = "Run one scan, review all candidate tabs, then use Cleanup all.";
    private string _progressMessage = string.Empty;
    private string _resultSummary = string.Empty;
    private double _progressPercentage;
    private bool _isBusy;

    public CompositeCleanupWorkspaceViewModel(
        ExecuteCompositeCleanupUseCase execute,
        ICompositeCleanupDialogService dialog)
    {
        _execute = execute;
        _dialog = dialog;
        CleanupAllCommand = new AsyncRelayCommand(CleanupAllAsync, CanCleanupAll);
    }

    public IAsyncRelayCommand CleanupAllCommand { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ProgressMessage { get => _progressMessage; private set => SetProperty(ref _progressMessage, value); }
    public string ResultSummary { get => _resultSummary; private set => SetProperty(ref _resultSummary, value); }
    public double ProgressPercentage { get => _progressPercentage; private set => SetProperty(ref _progressPercentage, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) CleanupAllCommand.NotifyCanExecuteChanged();
        }
    }

    public void UpdateContext(
        LibrarySnapshot? snapshot,
        IReadOnlyList<ExactDuplicateGroupRowViewModel> exactGroups,
        IReadOnlyList<MetadataDuplicateGroupRowViewModel> metadataGroups,
        IReadOnlyList<ExpandedCandidateGroupRowViewModel> expandedGroups)
    {
        Unsubscribe();
        _snapshot = snapshot;
        _exactGroups = exactGroups;
        _metadataGroups = metadataGroups;
        _expandedGroups = expandedGroups;
        Subscribe();
        int exact = ExactSelections().Count(value => !value.Skip);
        int metadata = MetadataSelections().Count(value => !value.Skip);
        int expanded = ExpandedSelections().Count(value => !value.Skip);
        Status = snapshot is null
            ? "Run or load authoritative state from one completed scan before Cleanup all."
            : $"Reviewed selections ready: {exact:N0} exact, {metadata:N0} metadata, {expanded:N0} expanded. Conflicts are checked before backup confirmation or mutation.";
        CleanupAllCommand.NotifyCanExecuteChanged();
    }

    private bool CanCleanupAll() => !IsBusy && _snapshot is not null
        && (ExactSelections().Any(value => !value.Skip)
            || MetadataSelections().Any(value => !value.Skip)
            || ExpandedSelections().Any(value => !value.Skip));

    private async Task CleanupAllAsync()
    {
        if (_snapshot is null) return;
        ExactDuplicateKeeperSelection[] exact = ExactSelections();
        MetadataCandidateCleanupSelection[] metadata = MetadataSelections();
        ExpandedCandidateCleanupSelection[] expanded = ExpandedSelections();
        CompositeCleanupBuildResult build = _execute.Build(new(
            _snapshot.Identity.LibraryRoot, exact, metadata, expanded));
        if (!build.IsValid)
        {
            string description = DescribeConflicts(build.Conflicts, _snapshot);
            _dialog.ShowConflicts(description);
            Status = $"Cleanup all blocked by {build.Conflicts.Count:N0} conflict(s). Change keeper or Skip selections and retry.";
            return;
        }
        if (!_dialog.ConfirmExternalBackup(build.Summary!))
        {
            Status = "Cleanup all canceled. Confirm a complete external backup before retrying.";
            return;
        }

        IsBusy = true;
        ProgressPercentage = 0;
        ProgressMessage = string.Empty;
        ResultSummary = string.Empty;
        try
        {
            Progress<CompositeCleanupProgress> progress = new(value =>
            {
                ProgressMessage = value.Message;
                ProgressPercentage = value.TotalOperations == 0 ? 0
                    : 100d * value.CompletedOperations / value.TotalOperations;
            });
            CompositeCleanupResult result = await _execute.ExecuteAsync(new(
                _snapshot.Identity.LibraryRoot,
                exact,
                metadata,
                expanded,
                ExternalBackupConfirmed: true), progress, CancellationToken.None).ConfigureAwait(true);
            if (result.Conflicts.Count > 0)
            {
                _dialog.ShowConflicts(DescribeConflicts(result.Conflicts, _snapshot));
                Status = "Cleanup all stopped because authoritative state or selections changed. Review conflicts and retry.";
                return;
            }
            ResultSummary = $"Transferred {result.TransferredFormatCount:N0} format(s), removed "
                + $"{result.RemovedFormatCount:N0} format(s), and deleted {result.RemovedRecordCount:N0} record(s).";
            Status = result.IsCompleted
                ? "Cleanup all completed. Run Scan to refresh analysis and inferred groups."
                : string.Join(" ", result.Issues.Select(value => $"{value.Code}: {value.Explanation}"));
        }
        finally
        {
            IsBusy = false;
            CleanupAllCommand.NotifyCanExecuteChanged();
        }
    }

    private ExactDuplicateKeeperSelection[] ExactSelections() => _exactGroups
        .Where(value => value.IsCleanupEligible && value.RetainedMember is not null)
        .Select(value => new ExactDuplicateKeeperSelection(
            value.GroupId, value.RetainedMember!.Member, value.Skip))
        .ToArray();

    private MetadataCandidateCleanupSelection[] MetadataSelections() => _metadataGroups
        .Select(value => new MetadataCandidateCleanupSelection(
            value.GroupId,
            value.KeeperMember is null ? null : new CalibreBookId(value.KeeperMember.BookId),
            value.Skip))
        .ToArray();

    private ExpandedCandidateCleanupSelection[] ExpandedSelections() => _expandedGroups
        .Select(value => new ExpandedCandidateCleanupSelection(
            new(value.GroupId),
            value.KeeperMember is null ? null : new CalibreBookId(value.KeeperMember.BookId),
            value.Skip))
        .ToArray();

    private static string DescribeConflicts(
        IReadOnlyList<CompositeCleanupConflict> conflicts,
        LibrarySnapshot snapshot)
    {
        Dictionary<CalibreBookId, CalibreBook> books = snapshot.Books.ToDictionary(value => value.Id);
        StringBuilder message = new("Cleanup all found incompatible reviewed selections. No mutation was started.\n\n");
        foreach (CompositeCleanupConflict conflict in conflicts.Take(50))
        {
            string records = string.Join(", ", conflict.RecordIds.Select(id =>
                books.TryGetValue(id, out CalibreBook? book)
                    ? $"{id.Value.ToString(CultureInfo.InvariantCulture)} ({book.Title})"
                    : id.Value.ToString(CultureInfo.InvariantCulture)));
            message.Append("• ").Append(conflict.Category).Append(" / ").Append(conflict.Code)
                .Append(": ").Append(conflict.Description);
            if (records.Length > 0) message.Append(" Records: ").Append(records).Append('.');
            if (conflict.Format is not null) message.Append(" Format: ").Append(conflict.Format).Append('.');
            message.AppendLine().AppendLine();
        }
        if (conflicts.Count > 50)
            message.Append("Additional conflicts omitted: ").Append(conflicts.Count - 50).Append('.');
        message.AppendLine("Change the relevant Keep/Skip selections and press Cleanup all again.");
        return message.ToString();
    }

    private void Subscribe()
    {
        foreach (ExactDuplicateGroupRowViewModel group in _exactGroups) group.PropertyChanged += OnSelectionChanged;
        foreach (MetadataDuplicateGroupRowViewModel group in _metadataGroups) group.PropertyChanged += OnSelectionChanged;
        foreach (ExpandedCandidateGroupRowViewModel group in _expandedGroups) group.PropertyChanged += OnSelectionChanged;
    }

    private void Unsubscribe()
    {
        foreach (ExactDuplicateGroupRowViewModel group in _exactGroups) group.PropertyChanged -= OnSelectionChanged;
        foreach (MetadataDuplicateGroupRowViewModel group in _metadataGroups) group.PropertyChanged -= OnSelectionChanged;
        foreach (ExpandedCandidateGroupRowViewModel group in _expandedGroups) group.PropertyChanged -= OnSelectionChanged;
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ExactDuplicateGroupRowViewModel.RetainedMember)
            or nameof(ExactDuplicateGroupRowViewModel.Skip)
            or nameof(MetadataDuplicateGroupRowViewModel.KeeperMember)
            or nameof(MetadataDuplicateGroupRowViewModel.Skip)
            or nameof(ExpandedCandidateGroupRowViewModel.KeeperMember)
            or nameof(ExpandedCandidateGroupRowViewModel.Skip))
            UpdateContext(_snapshot, _exactGroups, _metadataGroups, _expandedGroups);
    }
}
