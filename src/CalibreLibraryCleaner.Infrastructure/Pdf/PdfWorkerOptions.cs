using System.Diagnostics;

namespace CalibreLibraryCleaner.Infrastructure.Pdf;

public sealed record PdfWorkerOptions
{
    public string ExecutablePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "CalibreLibraryCleaner.PdfWorker.exe");

    internal Func<Process, long, IDisposable?> JobObjectFactory { get; init; } =
        static (process, memoryLimit) => PdfWorkerJobObject.TryCreateAndAssign(process, memoryLimit);
}
