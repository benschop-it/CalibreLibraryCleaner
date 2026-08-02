using System.Security.Cryptography;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.Execution;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

public sealed class ExactDuplicateFormatStagingTests
{
    [Fact]
    public async Task StageCopiesVerifiedBytesOutsideLibraryAndCleanupRemovesWorkspace()
    {
        using TemporaryDirectory directory = new();
        string library = Path.Combine(directory.Path, "library");
        string stagingRoot = Path.Combine(directory.Path, "staging");
        string relative = "Author/Book (1)/book.pdf";
        string source = Path.Combine(library, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        byte[] bytes = "verified-pdf"u8.ToArray();
        await File.WriteAllBytesAsync(source, bytes);
        FileInfo info = new(source);
        FormatFileFingerprint fingerprint = new(bytes.Length,
            new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        BookFormat format = new("PDF", "book", relative, FormatFileStatus.Present,
            fingerprint, new(info.Length, new(info.CreationTimeUtc, TimeSpan.Zero),
                new(info.LastWriteTimeUtc, TimeSpan.Zero), (int)info.Attributes));
        CleanupExecutionId executionId = new(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        FileExactDuplicateFormatStaging staging = new(new()
        {
            TransferStagingRoot = stagingRoot,
        });

        StagedExactDuplicateFormat staged = await staging.StageAsync(executionId,
            library, new(1), format, CancellationToken.None);

        File.Exists(staged.PhysicalPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(staged.PhysicalPath)).Should().Equal(bytes);
        Path.GetFullPath(staged.PhysicalPath).Should().StartWith(Path.GetFullPath(stagingRoot));
        await staging.CleanupAsync(executionId, CancellationToken.None);
        Directory.Exists(Path.Combine(stagingRoot, executionId.Value.ToString("N"))).Should().BeFalse();
    }
}
