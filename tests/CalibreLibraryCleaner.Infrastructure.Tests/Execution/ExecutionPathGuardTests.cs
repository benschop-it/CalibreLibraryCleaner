using CalibreLibraryCleaner.Infrastructure.Execution;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

public sealed class ExecutionPathGuardTests
{
    [Fact]
    public void AbsentFixedSiblingIsAccepted()
    {
        using Fixture fixture = new();

        bool accepted = ExecutionPathGuard.TryValidateQuarantineDestination(
            fixture.Source,
            fixture.Destination,
            out string? canonical,
            out bool exists,
            out string? reason);

        accepted.Should().BeTrue(reason);
        canonical.Should().Be(fixture.Destination);
        exists.Should().BeFalse();
    }

    [Fact]
    public void ExistingPhysicalCalibreSiblingIsAcceptedForReuse()
    {
        using Fixture fixture = new();
        Directory.CreateDirectory(fixture.Destination);
        File.WriteAllBytes(Path.Combine(fixture.Destination, "metadata.db"), []);

        bool accepted = ExecutionPathGuard.TryValidateQuarantineDestination(
            fixture.Source,
            fixture.Destination,
            out _,
            out bool exists,
            out string? reason);

        accepted.Should().BeTrue(reason);
        exists.Should().BeTrue();
    }

    [Theory]
    [InlineData("inside")]
    [InlineData("unrelated")]
    public void AnyPathOtherThanFixedSiblingIsRejected(string kind)
    {
        using Fixture fixture = new();
        string candidate = kind == "inside"
            ? Path.Combine(fixture.Source, "Unresolvable")
            : Path.Combine(fixture.Root, "Other");

        bool accepted = ExecutionPathGuard.TryValidateQuarantineDestination(
            fixture.Source,
            candidate,
            out _,
            out _,
            out string? reason);

        accepted.Should().BeFalse();
        reason.Should().Contain("fixed sibling");
    }

    [Fact]
    public void ExistingDestinationWithoutMetadataDatabaseIsRejected()
    {
        using Fixture fixture = new();
        Directory.CreateDirectory(fixture.Destination);

        bool accepted = ExecutionPathGuard.TryValidateQuarantineDestination(
            fixture.Source,
            fixture.Destination,
            out _,
            out _,
            out string? reason);

        accepted.Should().BeFalse();
        reason.Should().Contain("physical Calibre library");
    }

    [Fact]
    public void CalibreLibraryRootLimitIsEnforcedBeforeCreation()
    {
        using Fixture fixture = new(sourceName: new string('a', 60));

        bool accepted = ExecutionPathGuard.TryValidateQuarantineDestination(
            fixture.Source,
            fixture.Destination,
            out _,
            out _,
            out string? reason);

        accepted.Should().BeFalse();
        reason.Should().Contain("path limit");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(string sourceName = "Source")
        {
            Root = Path.Combine(Path.GetTempPath(), "clc-path-" + Guid.NewGuid().ToString("N")[..8]);
            Source = Path.Combine(Root, sourceName);
            Destination = Path.Combine(Root, sourceName + " - Unresolvable");
            Directory.CreateDirectory(Source);
            File.WriteAllBytes(Path.Combine(Source, "metadata.db"), []);
        }

        public string Root { get; }
        public string Source { get; }
        public string Destination { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
