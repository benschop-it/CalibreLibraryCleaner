using System.IO;
using CalibreLibraryCleaner.Wpf;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests;

public sealed class ReleasePackageSmokeCheckTests
{
    private static readonly string[] RequiredFiles =
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

    [Fact]
    public void CompleteReleaseLayoutAndEmbeddedWorkerPass()
    {
        using TemporaryPackage package = new();
        package.CreateRequiredFiles();

        ReleasePackageSmokeCheck.Validate(package.Root).Should().BeTrue();
    }

    [Fact]
    public void MissingPdfWorkerFails()
    {
        using TemporaryPackage package = new();
        package.CreateRequiredFiles();
        File.Delete(Path.Combine(package.Root, "pdf-worker", "CalibreLibraryCleaner.PdfWorker.exe"));

        ReleasePackageSmokeCheck.Validate(package.Root).Should().BeFalse();
    }

    [Fact]
    public void SymbolFileFails()
    {
        using TemporaryPackage package = new();
        package.CreateRequiredFiles();
        File.WriteAllBytes(Path.Combine(package.Root, "application.pdb"), [0x00]);

        ReleasePackageSmokeCheck.Validate(package.Root).Should().BeFalse();
    }

    private sealed class TemporaryPackage : IDisposable
    {
        public TemporaryPackage()
        {
            Root = Path.Combine(Path.GetTempPath(), "CalibreLibraryCleaner-package-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "pdf-worker"));
        }

        public string Root { get; }

        public void CreateRequiredFiles()
        {
            foreach (string relative in RequiredFiles)
            {
                File.WriteAllBytes(Path.Combine(Root, relative), [0x00]);
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
