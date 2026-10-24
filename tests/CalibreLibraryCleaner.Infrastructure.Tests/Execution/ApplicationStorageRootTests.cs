using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Infrastructure.Bibliographic;
using CalibreLibraryCleaner.Infrastructure.Calibre;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

public sealed class ApplicationStorageRootTests
{
    [Fact]
    public void IsolatedOfflineCompositionKeepsStateCredentialsAndWorkersInsideTheSelectedRoot()
    {
        using TemporaryDirectory temporary = new();
        ApplicationStorageRoot storage = new(temporary.Path);
        using ServiceProvider services = new ServiceCollection()
            .AddCalibreLibraryInfrastructure(storage, onlineMetadataEnabled: false).BuildServiceProvider();

        services.GetRequiredService<LibrarySnapshotStorageOptions>().StorageRoot
            .Should().Be(storage.GetPath("library-snapshots"));
        services.GetRequiredService<GoogleBooksApiKeyStoreOptions>().StorageRoot
            .Should().Be(storage.GetPath("credentials"));
        services.GetRequiredService<CalibreExecutionOptions>().ControlledConfigDirectory
            .Should().Be(storage.GetPath("calibre-config", "9.x"));
        services.GetRequiredService<ExecutionStorageOptions>().LeaseRoot
            .Should().Be(storage.GetPath("leases"));
        services.GetRequiredService<EditionMetadataEnrichmentOptions>().Enabled.Should().BeFalse();
        services.GetRequiredService<OpenLibraryOptions>().Enabled.Should().BeFalse();
    }

    [Fact]
    public void LibraryManagedAndEscapingLocationsAreNotSilentlyAccepted()
    {
        using TemporaryDirectory temporary = new();
        string library = Path.Combine(temporary.Path, "library");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "metadata.db"), "generated-marker");

        Action inside = () => _ = new ApplicationStorageRoot(Path.Combine(library, "state"));
        inside.Should().Throw<InvalidOperationException>();
        ApplicationStorageRoot root = new(Path.Combine(temporary.Path, "state"));
        Action escape = () => root.GetPath("..", "elsewhere");
        escape.Should().Throw<ArgumentException>();
        Action relative = () => _ = new ApplicationStorageRoot("relative");
        relative.Should().Throw<ArgumentException>();
    }
}
