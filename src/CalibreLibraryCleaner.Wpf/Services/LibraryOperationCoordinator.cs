namespace CalibreLibraryCleaner.Wpf.Services;

public sealed class LibraryOperationCoordinator
{
    private int _isOperationActive;

    public event EventHandler? StateChanged;

    public bool IsOperationActive => Volatile.Read(ref _isOperationActive) != 0;

    public IDisposable? TryBegin()
    {
        if (Interlocked.CompareExchange(ref _isOperationActive, 1, 0) != 0)
        {
            return null;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return new OperationLease(this);
    }

    private void End()
    {
        if (Interlocked.Exchange(ref _isOperationActive, 0) != 0)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class OperationLease(LibraryOperationCoordinator owner) : IDisposable
    {
        private LibraryOperationCoordinator? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.End();
    }
}
