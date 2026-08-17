using CalibreLibraryCleaner.Infrastructure.Pdf;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Pdf;

public sealed class PdfWorkerOptionsTests
{
    [Fact]
    public void DefaultExecutableUsesIsolatedWorkerDirectory()
    {
        PdfWorkerOptions options = new();

        options.ExecutablePath.Should().Be(Path.Combine(
            AppContext.BaseDirectory,
            "pdf-worker",
            "CalibreLibraryCleaner.PdfWorker.exe"));
    }
}
