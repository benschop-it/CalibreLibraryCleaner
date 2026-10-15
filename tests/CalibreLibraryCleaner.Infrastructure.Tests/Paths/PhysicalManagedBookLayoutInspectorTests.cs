using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.Paths;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Paths;

public sealed class PhysicalManagedBookLayoutInspectorTests
{
    [Fact]
    public async Task ReportsUnregisteredFormatAndDuplicateRecordDirectory()
    {
        using TemporaryDirectory library = new();
        string registeredDirectory = Directory.CreateDirectory(
            Path.Combine(library.Path, "Rendell, Ruth", "Asta's boek (33865)")).FullName;
        string registeredFile = Path.Combine(registeredDirectory, "Asta's boek - Rendell, Ruth.epub");
        await File.WriteAllTextAsync(registeredFile, "registered");
        await File.WriteAllTextAsync(
            Path.Combine(registeredDirectory, "Asta's Boek - Ruth Rendell.epub"), "extra");
        await File.WriteAllTextAsync(Path.Combine(registeredDirectory, "cover.jpg"), "cover");
        Directory.CreateDirectory(Path.Combine(library.Path, "Ruth Rendell", "Asta's boek (33865)"));
        PhysicalManagedBookLayoutInspector inspector = new();

        IReadOnlyList<ManagedBookLayoutIssue> issues = await inspector.InspectAsync(
            new(library.Path, Path.Combine(library.Path, "metadata.db")),
            [new(
                new(33865),
                "Rendell, Ruth/Asta's boek (33865)",
                ["Rendell, Ruth/Asta's boek (33865)/Asta's boek - Rendell, Ruth.epub"])],
            CancellationToken.None);

        issues.Should().BeEquivalentTo([
            new ManagedBookLayoutIssue(new(33865), ManagedBookLayoutProblem.UnregisteredFormatFile),
            new ManagedBookLayoutIssue(new(33865), ManagedBookLayoutProblem.DuplicateRecordDirectory),
        ]);
    }

    [Fact]
    public async Task CleanManagedDirectoryProducesNoIssues()
    {
        using TemporaryDirectory library = new();
        string directory = Directory.CreateDirectory(
            Path.Combine(library.Path, "Ruth Rendell", "Asta's boek (33865)")).FullName;
        await File.WriteAllTextAsync(Path.Combine(directory, "Asta's boek - Ruth Rendell.epub"), "registered");
        await File.WriteAllTextAsync(Path.Combine(directory, "metadata.opf"), "metadata");
        await File.WriteAllTextAsync(Path.Combine(directory, "cover.jpg"), "cover");
        PhysicalManagedBookLayoutInspector inspector = new();

        IReadOnlyList<ManagedBookLayoutIssue> issues = await inspector.InspectAsync(
            new(library.Path, Path.Combine(library.Path, "metadata.db")),
            [new(
                new(33865),
                "Ruth Rendell/Asta's boek (33865)",
                ["Ruth Rendell/Asta's boek (33865)/Asta's boek - Ruth Rendell.epub"])],
            CancellationToken.None);

        issues.Should().BeEmpty();
    }
}
