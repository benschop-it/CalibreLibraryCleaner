using System.Diagnostics;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

[Collection(ProcessEnvironmentGroup.Name)]
public sealed class CalibreEbookViewerLauncherTests
{
    [Fact]
    public async Task LaunchesTrustedViewerWithOneContainedBookArgument()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        string library = Path.Combine(temporary.Path, "library");
        string relative = "Author/Book (1)/book.epub";
        string book = Path.Combine(library, "Author", "Book (1)", "book.epub");
        Directory.CreateDirectory(Path.GetDirectoryName(book)!);
        await File.WriteAllBytesAsync(book, [1, 2, 3]);
        DateTime originalWriteTime = File.GetLastWriteTimeUtc(book);
        string staging = Path.Combine(temporary.Path, "staging");
        string? stagedPath = null;
        string log = Path.Combine(temporary.Path, "viewer.jsonl");
        executable.SetLogPath(log);
        CalibreEbookViewerLauncher launcher = new(new()
        {
            TrustedExecutablePath = executable.ExecutablePath,
            ViewerStagingRoot = staging,
        }, startInfo =>
        {
            startInfo.ArgumentList.Should().HaveCount(2);
            startInfo.ArgumentList[0].Should().Be("--new-instance");
            stagedPath = startInfo.ArgumentList[1];
            stagedPath.Should().NotBe(Path.GetFullPath(book));
            Path.GetFileName(stagedPath).Should().Be("book.epub");
            File.ReadAllBytes(stagedPath).Should().Equal(1, 2, 3);
            File.WriteAllBytes(stagedPath, [9, 8, 7, 6]);
            Process? process = Process.Start(startInfo);
            process.Should().NotBeNull();
            process!.WaitForExit(TimeSpan.FromSeconds(5)).Should().BeTrue();
            return process;
        });

        EbookViewerLaunchResult result = await launcher.LaunchAsync(
            new(library, relative), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        JsonSerializer.Deserialize<string[]>(File.ReadLines(log).Single())
            .Should().Equal("--new-instance", stagedPath);
        File.ReadAllBytes(book).Should().Equal(1, 2, 3);
        File.GetLastWriteTimeUtc(book).Should().Be(originalWriteTime);
        Directory.Exists(staging).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(staging).Should().BeEmpty();
    }

    [Fact]
    public async Task MissingViewerFailsWithoutLaunching()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        File.Delete(executable.ViewerExecutablePath);
        await File.WriteAllBytesAsync(Path.Combine(temporary.Path, "book.epub"), [1, 2, 3]);
        CalibreEbookViewerLauncher launcher = new(new()
        {
            TrustedExecutablePath = executable.ExecutablePath,
        });

        EbookViewerLaunchResult result = await launcher.LaunchAsync(
            new(temporary.Path, "book.epub"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("EBOOK_VIEWER_NOT_FOUND");
    }

    [Theory]
    [InlineData("../outside.epub")]
    [InlineData("missing.epub")]
    [InlineData("../outside.pdf")]
    [InlineData("missing.pdf")]
    public async Task UnsafeOrMissingBookPathFailsWithoutLaunching(string relativePath)
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        string log = Path.Combine(temporary.Path, "viewer.jsonl");
        executable.SetLogPath(log);
        CalibreEbookViewerLauncher launcher = new(new()
        {
            TrustedExecutablePath = executable.ExecutablePath,
        });

        EbookViewerLaunchResult result = await launcher.LaunchAsync(
            new(temporary.Path, relativePath), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("EBOOK_FILE_UNAVAILABLE");
        File.Exists(log).Should().BeFalse();
    }

    [Theory]
    [InlineData("book.pdf")]
    [InlineData("Book with spaces.PDF")]
    public async Task PdfUsesDefaultAssociationWithoutCalibreOrArguments(string relativePath)
    {
        using TemporaryDirectory temporary = new();
        string bookPath = Path.Combine(temporary.Path, relativePath);
        await File.WriteAllBytesAsync(bookPath, [1, 2, 3]);
        ProcessStartInfo? captured = null;
        CalibreEbookViewerLauncher launcher = new(new() { TrustedExecutablePath = "" }, startInfo =>
        {
            captured = startInfo;
            return null;
        });

        EbookViewerLaunchResult result = await launcher.LaunchAsync(
            new(temporary.Path, relativePath), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.FileName.Should().Be(Path.GetFullPath(bookPath));
        captured.UseShellExecute.Should().BeTrue();
        captured.ArgumentList.Should().BeEmpty();
        captured.Arguments.Should().BeEmpty();
    }

    [Fact]
    public async Task PdfAssociationFailureIsReportedWithoutThrowing()
    {
        using TemporaryDirectory temporary = new();
        await File.WriteAllBytesAsync(Path.Combine(temporary.Path, "book.pdf"), [1, 2, 3]);
        CalibreEbookViewerLauncher launcher = new(new(), _ => throw new System.ComponentModel.Win32Exception());

        EbookViewerLaunchResult result = await launcher.LaunchAsync(
            new(temporary.Path, "book.pdf"), CancellationToken.None);

        result.ErrorCode.Should().Be("EBOOK_VIEWER_START_FAILED");
        result.ErrorMessage.Should().Contain("default PDF viewer");
    }

    [Fact]
    public async Task UnsupportedFormatCannotBeShellExecuted()
    {
        using TemporaryDirectory temporary = new();
        await File.WriteAllBytesAsync(Path.Combine(temporary.Path, "book.cmd"), [1, 2, 3]);
        bool launched = false;
        CalibreEbookViewerLauncher launcher = new(new(), _ =>
        {
            launched = true;
            return null;
        });

        EbookViewerLaunchResult result = await launcher.LaunchAsync(
            new(temporary.Path, "book.cmd"), CancellationToken.None);

        result.ErrorCode.Should().Be("EBOOK_FORMAT_UNSUPPORTED");
        launched.Should().BeFalse();
    }

    [Fact]
    public async Task RunningViewerOwnsStagedCopyUntilExitThenDeletesIt()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        executable.SetSleepMilliseconds(300);
        string book = Path.Combine(temporary.Path, "library", "book.epub");
        Directory.CreateDirectory(Path.GetDirectoryName(book)!);
        await File.WriteAllBytesAsync(book, [1, 2, 3]);
        string staging = Path.Combine(temporary.Path, "staging");
        CalibreEbookViewerLauncher launcher = new(new()
        {
            TrustedExecutablePath = executable.ExecutablePath,
            ViewerStagingRoot = staging,
        });

        EbookViewerLaunchResult result = await launcher.LaunchAsync(
            new(Path.GetDirectoryName(book)!, "book.epub"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        Directory.EnumerateDirectories(staging).Should().ContainSingle();
        await WaitUntilAsync(() => !Directory.EnumerateFileSystemEntries(staging).Any());
        File.ReadAllBytes(book).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ViewerStartFailureDeletesStagedCopy()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        string book = Path.Combine(temporary.Path, "library", "book.epub");
        Directory.CreateDirectory(Path.GetDirectoryName(book)!);
        await File.WriteAllBytesAsync(book, [1, 2, 3]);
        string staging = Path.Combine(temporary.Path, "staging");
        CalibreEbookViewerLauncher launcher = new(new()
        {
            TrustedExecutablePath = executable.ExecutablePath,
            ViewerStagingRoot = staging,
        }, _ => throw new System.ComponentModel.Win32Exception());

        EbookViewerLaunchResult result = await launcher.LaunchAsync(
            new(Path.GetDirectoryName(book)!, "book.epub"), CancellationToken.None);

        result.ErrorCode.Should().Be("EBOOK_VIEWER_START_FAILED");
        Directory.EnumerateFileSystemEntries(staging).Should().BeEmpty();
        File.ReadAllBytes(book).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task ViewerStagingInsideLibraryAndOversizedEpubAreRejectedBeforeLaunch()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        string library = Path.Combine(temporary.Path, "library");
        string book = Path.Combine(library, "book.epub");
        Directory.CreateDirectory(library);
        await File.WriteAllBytesAsync(book, [1, 2, 3]);
        bool launched = false;
        CalibreEbookViewerLauncher inside = new(new()
        {
            TrustedExecutablePath = executable.ExecutablePath,
            ViewerStagingRoot = Path.Combine(library, "staging"),
        }, _ => { launched = true; return null; });
        CalibreEbookViewerLauncher oversized = new(new()
        {
            TrustedExecutablePath = executable.ExecutablePath,
            ViewerStagingRoot = Path.Combine(temporary.Path, "staging"),
            ViewerMaximumStagingBytes = 2,
        }, _ => { launched = true; return null; });

        (await inside.LaunchAsync(new(library, "book.epub"), CancellationToken.None))
            .ErrorCode.Should().Be("EBOOK_VIEWER_STAGING_FAILED");
        (await oversized.LaunchAsync(new(library, "book.epub"), CancellationToken.None))
            .ErrorCode.Should().Be("EBOOK_VIEWER_STAGING_FAILED");
        launched.Should().BeFalse();
    }

    [Fact]
    public async Task LaunchPrunesOnlyBoundedOldStagingDirectories()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        string library = Path.Combine(temporary.Path, "library");
        string book = Path.Combine(library, "book.epub");
        Directory.CreateDirectory(library);
        await File.WriteAllBytesAsync(book, [1, 2, 3]);
        string staging = Path.Combine(temporary.Path, "staging");
        Directory.CreateDirectory(staging);
        string old = Path.Combine(staging, "old");
        string recent = Path.Combine(staging, "recent");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(recent);
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        CalibreEbookViewerLauncher launcher = new(new()
        {
            TrustedExecutablePath = executable.ExecutablePath,
            ViewerStagingRoot = staging,
            ViewerMaximumStaleDirectoriesToInspect = 2,
            ViewerStaleAge = TimeSpan.FromDays(1),
        }, startInfo =>
        {
            Process? process = Process.Start(startInfo);
            process!.WaitForExit(TimeSpan.FromSeconds(5)).Should().BeTrue();
            return process;
        });

        (await launcher.LaunchAsync(new(library, "book.epub"), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(recent).Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }

}
