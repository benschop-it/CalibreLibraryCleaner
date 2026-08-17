using System.IO;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;

namespace CalibreLibraryCleaner.Wpf;

internal static class ReleasePackageSmokeCheck
{
    private const string MutationWorkerResource =
        "CalibreLibraryCleaner.Infrastructure.Calibre.calibre_mutation_worker.py";

    public static bool Validate(string baseDirectory)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
            string[] requiredFiles =
            [
                "CalibreLibraryCleaner.Wpf.exe",
                "CalibreLibraryCleaner.Wpf.dll",
                "CalibreLibraryCleaner.Wpf.deps.json",
                "CalibreLibraryCleaner.Wpf.runtimeconfig.json",
                "CalibreLibraryCleaner.Infrastructure.dll",
                Path.Combine("pdf-worker", "CalibreLibraryCleaner.PdfWorker.exe"),
                Path.Combine("pdf-worker", "CalibreLibraryCleaner.PdfWorker.dll"),
                Path.Combine("pdf-worker", "CalibreLibraryCleaner.PdfWorker.deps.json"),
                Path.Combine("pdf-worker", "CalibreLibraryCleaner.PdfWorker.runtimeconfig.json"),
            ];
            if (requiredFiles.Any(value => !File.Exists(Path.Combine(root, value)))) return false;
            if (Directory.EnumerateFiles(root, "*.pdb", SearchOption.AllDirectories).Any()) return false;
            using Stream? worker = typeof(ServiceCollectionExtensions).Assembly
                .GetManifestResourceStream(MutationWorkerResource);
            return worker is { Length: > 0 };
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return false;
        }
    }
}
