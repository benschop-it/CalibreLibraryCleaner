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
        string log = Path.Combine(temporary.Path, "viewer.jsonl");
        executable.SetLogPath(log);
        CalibreEbookViewerLauncher launcher = new(new()
        {
            TrustedExecutablePath = executable.ExecutablePath,
        });

        EbookViewerLaunchResult result = await launcher.LaunchAsync(
            new(library, relative), CancellationToken.None);
        await WaitUntilAsync(() => File.Exists(log) && File.ReadLines(log).Any());

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        JsonSerializer.Deserialize<string[]>(File.ReadLines(log).Single())
            .Should().Equal(Path.GetFullPath(book));
    }

    [Fact]
    public async Task MissingViewerFailsWithoutLaunching()
    {
        using ControlledCalibreExecutable executable = new();
        using TemporaryDirectory temporary = new();
        File.Delete(executable.ViewerExecutablePath);
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

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
