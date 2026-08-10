using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Infrastructure.Hashing;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Hashing;

public sealed class PhysicalFormatFileProbeTests
{
    [Fact]
    public async Task ReadablePhysicalFileReturnsCurrentObservation()
    {
        using TemporaryDirectory directory = new();
        string libraryRoot = Path.Combine(directory.Path, "library");
        string relativePath = Path.Combine("Author", "Book.epub");
        string fullPath = Path.Combine(libraryRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, [1, 2, 3, 4]);
        PhysicalFormatFileProbe probe = new();

        FormatFileProbeResult result = await probe.ProbeAsync(
            new(libraryRoot, fullPath, relativePath), CancellationToken.None);

        result.Status.Should().Be(FormatFileProbeStatus.Success);
        result.Observation.Should().NotBeNull();
        result.Observation!.Length.Should().Be(4);
        result.ReasonCode.Should().BeNull();
    }

    [Fact]
    public async Task MissingPhysicalFileFailsClosed()
    {
        using TemporaryDirectory directory = new();
        string libraryRoot = Path.Combine(directory.Path, "library");
        Directory.CreateDirectory(libraryRoot);
        string relativePath = "missing.epub";
        PhysicalFormatFileProbe probe = new();

        FormatFileProbeResult result = await probe.ProbeAsync(
            new(libraryRoot, Path.Combine(libraryRoot, relativePath), relativePath),
            CancellationToken.None);

        result.Status.Should().Be(FormatFileProbeStatus.Missing);
        result.Observation.Should().BeNull();
    }

    [Fact]
    public async Task ExclusivelyLockedFileIsInaccessible()
    {
        using TemporaryDirectory directory = new();
        string libraryRoot = Path.Combine(directory.Path, "library");
        Directory.CreateDirectory(libraryRoot);
        string relativePath = "locked.epub";
        string fullPath = Path.Combine(libraryRoot, relativePath);
        await File.WriteAllBytesAsync(fullPath, [1, 2, 3]);
        PhysicalFormatFileProbe probe = new();

        FormatFileProbeResult result;
        await using (FileStream locked = new(
                         fullPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await probe.ProbeAsync(
                new(libraryRoot, fullPath, relativePath), CancellationToken.None);
        }

        result.Status.Should().Be(FormatFileProbeStatus.Inaccessible);
        result.Observation.Should().BeNull();
    }

    [Fact]
    public async Task ReparsePointInParentChainIsUnsafe()
    {
        using TemporaryDirectory directory = new();
        string libraryRoot = Path.Combine(directory.Path, "library");
        string outsideRoot = Path.Combine(directory.Path, "outside");
        string linkedDirectory = Path.Combine(libraryRoot, "Linked author");
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(outsideRoot);
        await File.WriteAllBytesAsync(Path.Combine(outsideRoot, "Book.epub"), [1, 2, 3]);
        Directory.CreateSymbolicLink(linkedDirectory, outsideRoot);
        PhysicalFormatFileProbe probe = new();

        FormatFileProbeResult result = await probe.ProbeAsync(
            new(
                libraryRoot,
                Path.Combine(linkedDirectory, "Book.epub"),
                Path.Combine("Linked author", "Book.epub")),
            CancellationToken.None);

        result.Status.Should().Be(FormatFileProbeStatus.UnsafePath);
        result.Observation.Should().BeNull();
    }
}
