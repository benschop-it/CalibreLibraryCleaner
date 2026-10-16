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
    [InlineData("Book ", "EPUB")]
    [InlineData("Book.", "EPUB")]
    [InlineData("Book . ", "PDF")]
    public void ResolvePreservesValidSpacesAndDotsBeforeFormatExtension(string storedName, string format)
    {
        using SyntheticCalibreLibrary library = new();
        using ServiceProvider provider = TestServices.CreateProvider();
        ILibraryPathResolver resolver = provider.GetRequiredService<ILibraryPathResolver>();
        string directory = Path.Combine(library.RootPath, "Author", "Book (1)");
        Directory.CreateDirectory(directory);
        string expectedPath = Path.Combine(directory, $"{storedName}.{format.ToLowerInvariant()}");
        File.WriteAllBytes(expectedPath, [1, 2, 3]);

        ResolvedFormatPathOutcome outcome = resolver.ResolveFormat(
            new(library.RootPath, library.DatabasePath), "Author/Book (1)", storedName, format);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Path!.FullPath.Should().Be(expectedPath);
        File.Exists(outcome.Path.FullPath).Should().BeTrue();
        File.ReadAllBytes(outcome.Path.FullPath).Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("CON ")]
    [InlineData("CON .data")]
    [InlineData("NUL ")]
    [InlineData("COM1 ")]
    [InlineData("LPT9.")]
    [InlineData("../Outside")]
    [InlineData("Book/Other")]
    [InlineData("Book\\Other")]
    [InlineData("Book:stream")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(" ")]
    public void ResolveRejectsUnsafeStoredFormatStem(string storedName)
    {
        using SyntheticCalibreLibrary library = new();
        using ServiceProvider provider = TestServices.CreateProvider();
        ILibraryPathResolver resolver = provider.GetRequiredService<ILibraryPathResolver>();

        ResolvedFormatPathOutcome outcome = resolver.ResolveFormat(
            new(library.RootPath, library.DatabasePath), "Author/Book (1)", storedName, "EPUB");

        outcome.IsSuccess.Should().BeFalse();
    }

    [Theory]
    [InlineData("../Outside")]
    [InlineData("Author//Book")]
    [InlineData("/Rooted")]
    [InlineData("Author/CON")]
    [InlineData("Author/CON .folder")]
    [InlineData("Author/Book ")]
    [InlineData("Author/Book.")]
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
