using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Paths;

public sealed class LibraryPathResolverTests
{
    [Fact]
    public async Task ValidateFindsTopLevelMetadataDatabase()
    {
        using SyntheticCalibreLibrary library = new();
        using ServiceProvider provider = TestServices.CreateProvider();
        ILibraryPathResolver resolver = provider.GetRequiredService<ILibraryPathResolver>();

        LibraryValidationOutcome outcome = await resolver.ValidateAsync(
            library.RootPath,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Location!.MetadataDatabasePath.Should().Be(library.DatabasePath);
    }

    [Fact]
    public void ResolveBuildsExactLowercaseExtensionPath()
    {
        using SyntheticCalibreLibrary library = new();
        using ServiceProvider provider = TestServices.CreateProvider();
        ILibraryPathResolver resolver = provider.GetRequiredService<ILibraryPathResolver>();
        ValidatedLibraryLocation location = new(library.RootPath, library.DatabasePath);

        ResolvedFormatPathOutcome outcome = resolver.ResolveFormat(
            location,
            "Author/Book (1)",
            "Book",
            "EPUB");

        outcome.IsSuccess.Should().BeTrue();
        outcome.Path!.FullPath.Should().Be(Path.Combine(library.RootPath, "Author", "Book (1)", "Book.epub"));
    }

    [Theory]
    [InlineData("../Outside")]
    [InlineData("Author//Book")]
    [InlineData("/Rooted")]
    [InlineData("Author/CON")]
    public void ResolveRejectsUnsafeManagedDirectory(string managedDirectory)
    {
        using SyntheticCalibreLibrary library = new();
        using ServiceProvider provider = TestServices.CreateProvider();
        ILibraryPathResolver resolver = provider.GetRequiredService<ILibraryPathResolver>();

        ResolvedFormatPathOutcome outcome = resolver.ResolveFormat(
            new(library.RootPath, library.DatabasePath),
            managedDirectory,
            "Book",
            "EPUB");

        outcome.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ValidateHonorsPreCanceledToken()
    {
        using SyntheticCalibreLibrary library = new();
        using ServiceProvider provider = TestServices.CreateProvider();
        ILibraryPathResolver resolver = provider.GetRequiredService<ILibraryPathResolver>();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> act = async () => await resolver.ValidateAsync(library.RootPath, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
