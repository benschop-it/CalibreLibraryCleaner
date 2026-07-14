using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CalibreLibraryCleaner.Wpf.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ValidateLibraryUseCase _validateLibrary;
    private readonly ScanLibraryUseCase _scanLibrary;
    private readonly ILibraryFolderPicker _folderPicker;
    private readonly BulkObservableCollection<BookRowViewModel> _books = [];
    private readonly BulkObservableCollection<ExactDuplicateGroupRowViewModel> _exactDuplicateGroups = [];
    private CancellationTokenSource? _scanCancellation;
    private string _selectedLibraryPath = string.Empty;
    private string _statusMessage = "Choose a Calibre library folder.";
    private string _errorMessage = string.Empty;
    private string _errorAction = string.Empty;
    private string _exactDuplicateSummary = "No exact file duplicate groups have been found.";
    private bool _isBusy;
    private double _progressPercentage;
    private bool _isProgressIndeterminate;
    private BookRowViewModel? _selectedBook;
    private ExactDuplicateGroupRowViewModel? _selectedExactDuplicateGroup;

    public MainWindowViewModel(
        ValidateLibraryUseCase validateLibrary,
        ScanLibraryUseCase scanLibrary,
        ILibraryFolderPicker folderPicker)
    {
        _validateLibrary = validateLibrary;
        _scanLibrary = scanLibrary;
        _folderPicker = folderPicker;
        Books = new ReadOnlyObservableCollection<BookRowViewModel>(_books);
        ExactDuplicateGroups = new ReadOnlyObservableCollection<ExactDuplicateGroupRowViewModel>(_exactDuplicateGroups);
        SelectLibraryCommand = new AsyncRelayCommand(SelectLibraryAsync, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedLibraryPath));
        CancelCommand = new RelayCommand(
            CancelScan,
            () => IsBusy && _scanCancellation is { IsCancellationRequested: false });
    }

    public string SelectedLibraryPath
    {
        get => _selectedLibraryPath;
        private set
        {
            if (SetProperty(ref _selectedLibraryPath, value))
            {
                ScanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public string ErrorAction
    {
        get => _errorAction;
        private set => SetProperty(ref _errorAction, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SelectLibraryCommand.NotifyCanExecuteChanged();
                ScanCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public double ProgressPercentage
    {
        get => _progressPercentage;
        private set => SetProperty(ref _progressPercentage, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public ReadOnlyObservableCollection<BookRowViewModel> Books { get; }

    public ReadOnlyObservableCollection<ExactDuplicateGroupRowViewModel> ExactDuplicateGroups { get; }

    public string ExactDuplicateSummary
    {
        get => _exactDuplicateSummary;
        private set => SetProperty(ref _exactDuplicateSummary, value);
    }

    public BookRowViewModel? SelectedBook
    {
        get => _selectedBook;
        set
        {
            if (SetProperty(ref _selectedBook, value))
            {
                OnPropertyChanged(nameof(SelectedFormats));
            }
        }
    }

    public IReadOnlyList<FormatRowViewModel> SelectedFormats => SelectedBook?.Formats ?? [];

    public ExactDuplicateGroupRowViewModel? SelectedExactDuplicateGroup
    {
        get => _selectedExactDuplicateGroup;
        set
        {
            if (SetProperty(ref _selectedExactDuplicateGroup, value))
            {
                OnPropertyChanged(nameof(SelectedExactDuplicateMembers));
            }
        }
    }

    public IReadOnlyList<ExactDuplicateMemberRowViewModel> SelectedExactDuplicateMembers =>
        SelectedExactDuplicateGroup?.Members ?? [];

    public IAsyncRelayCommand SelectLibraryCommand { get; }

    public IAsyncRelayCommand ScanCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public void Dispose()
    {
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
    }

    private async Task SelectLibraryAsync()
    {
        string? selected = _folderPicker.PickFolder(SelectedLibraryPath);
        if (selected is null)
        {
            return;
        }

        SelectedLibraryPath = selected;
        ClearError();
        LibraryValidationOutcome outcome = await _validateLibrary
            .ExecuteAsync(selected, CancellationToken.None)
            .ConfigureAwait(true);
        if (outcome.IsSuccess)
        {
            StatusMessage = "Library folder is valid. Select Scan to load it.";
        }
        else
        {
            ShowError(outcome.Error!);
            StatusMessage = "Library validation failed.";
        }
    }

    private async Task ScanAsync()
    {
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        IsBusy = true;
        ClearError();
        ProgressPercentage = 0;
        IsProgressIndeterminate = true;
        CoalescingProgress progress = new(UpdateProgress);

        try
        {
            LibraryScanOutcome outcome = await _scanLibrary
                .ExecuteAsync(SelectedLibraryPath, progress, _scanCancellation.Token)
                .ConfigureAwait(true);
            progress.Complete();
            if (outcome.IsSuccess)
            {
                SnapshotPresentation presentation = await Task.Run(
                        () => CreatePresentation(outcome.Snapshot!, _scanCancellation.Token),
                        _scanCancellation.Token)
                    .ConfigureAwait(true);
                ApplySnapshot(outcome.Snapshot!, presentation);
            }
            else
            {
                ShowError(outcome.Error!);
                StatusMessage = "Library scan failed. Previous results, if any, were not replaced.";
            }
        }
        catch (OperationCanceledException)
        {
            progress.Complete();
            StatusMessage = "Scan canceled. Previous results, if any, were not replaced.";
        }
        finally
        {
            progress.Complete();
            IsBusy = false;
            _scanCancellation.Dispose();
            _scanCancellation = null;
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private void CancelScan()
    {
        _scanCancellation?.Cancel();
        CancelCommand.NotifyCanExecuteChanged();
        StatusMessage = "Canceling scan...";
    }

    private void UpdateProgress(LibraryScanProgress progress)
    {
        StatusMessage = progress.Message;
        IsProgressIndeterminate = progress.TotalUnits is null;
        ProgressPercentage = progress.TotalUnits is > 0
            ? 100d * progress.CompletedUnits / progress.TotalUnits.Value
            : progress.Phase == LibraryScanPhase.Completed ? 100 : 0;
    }

    private static SnapshotPresentation CreatePresentation(
        LibrarySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        BookRowViewModel[] books = new BookRowViewModel[snapshot.Books.Count];
        Dictionary<CalibreBookId, CalibreBook> booksById = new(snapshot.Books.Count);
        for (int index = 0; index < snapshot.Books.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CalibreBook book = snapshot.Books[index];
            books[index] = new(book);
            booksById.Add(book.Id, book);
        }

        ExactDuplicateGroupRowViewModel[] groups = new ExactDuplicateGroupRowViewModel[
            snapshot.ExactBinaryDuplicateGroups.Count];
        for (int index = 0; index < snapshot.ExactBinaryDuplicateGroups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            groups[index] = new(snapshot.ExactBinaryDuplicateGroups[index], booksById);
        }

        int missingCount = 0;
        foreach (Domain.Findings.LibraryFinding finding in snapshot.Findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (finding.Code == "FORMAT_FILE_MISSING")
            {
                missingCount++;
            }
        }

        return new(books, groups, missingCount);
    }

    private void ApplySnapshot(LibrarySnapshot snapshot, SnapshotPresentation presentation)
    {
        _books.ReplaceAll(presentation.Books);
        SelectedBook = _books.FirstOrDefault();
        _exactDuplicateGroups.ReplaceAll(presentation.Groups);
        SelectedExactDuplicateGroup = _exactDuplicateGroups.FirstOrDefault();
        ExactDuplicateSummary = presentation.Groups.Count == 0
            ? "No exact file duplicate groups were found in this scan."
            : presentation.Groups.Count == 1
                ? "1 exact file duplicate group was found."
                : $"{presentation.Groups.Count} exact file duplicate groups were found.";
        StatusMessage = snapshot.Books.Count == 0
            ? "Scan complete. The library contains no books."
            : $"Scan complete: {snapshot.Books.Count} books, {snapshot.ExactBinaryDuplicateGroups.Count} exact file duplicate groups, {presentation.MissingCount} missing format files.";
        IsProgressIndeterminate = false;
        ProgressPercentage = 100;
    }

    private void ClearError()
    {
        ErrorMessage = string.Empty;
        ErrorAction = string.Empty;
    }

    private void ShowError(LibraryError error)
    {
        ErrorMessage = error.Message;
        ErrorAction = error.SuggestedAction;
    }

    private sealed record SnapshotPresentation(
        IReadOnlyList<BookRowViewModel> Books,
        IReadOnlyList<ExactDuplicateGroupRowViewModel> Groups,
        int MissingCount);

    private sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        public void ReplaceAll(IEnumerable<T> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            CheckReentrancy();
            Items.Clear();
            foreach (T value in values)
            {
                Items.Add(value);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    private sealed class CoalescingProgress : IProgress<LibraryScanProgress>
    {
        private readonly object _gate = new();
        private readonly Action<LibraryScanProgress> _handler;
        private readonly SynchronizationContext? _synchronizationContext = SynchronizationContext.Current;
        private LibraryScanProgress? _latest;
        private bool _isActive = true;
        private bool _isScheduled;

        public CoalescingProgress(Action<LibraryScanProgress> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            _handler = handler;
        }

        public void Report(LibraryScanProgress value)
        {
            lock (_gate)
            {
                if (!_isActive)
                {
                    return;
                }

                _latest = value;
                if (_isScheduled)
                {
                    return;
                }

                _isScheduled = true;
            }

            PostDrain();
        }

        public void Complete()
        {
            lock (_gate)
            {
                _isActive = false;
                _latest = null;
            }
        }

        private void PostDrain()
        {
            if (_synchronizationContext is null)
            {
                ThreadPool.QueueUserWorkItem(static state => ((CoalescingProgress)state!).Drain(), this);
            }
            else
            {
                _synchronizationContext.Post(static state => ((CoalescingProgress)state!).Drain(), this);
            }
        }

        private void Drain()
        {
            LibraryScanProgress? value;
            lock (_gate)
            {
                value = _isActive ? _latest : null;
                _latest = null;
                _isScheduled = false;
            }

            if (value is not null)
            {
                _handler(value);
            }

            lock (_gate)
            {
                if (!_isActive || _latest is null || _isScheduled)
                {
                    return;
                }

                _isScheduled = true;
            }

            PostDrain();
        }
    }
}
