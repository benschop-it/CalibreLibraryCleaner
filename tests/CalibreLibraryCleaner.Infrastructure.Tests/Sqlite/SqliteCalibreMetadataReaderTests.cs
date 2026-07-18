using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Sqlite;

public sealed class SqliteCalibreMetadataReaderTests
{
    [Fact]
    public async Task ReadLoadsRequestedCatalogFieldsInCalibreOrder()
    {
        using SyntheticCalibreLibrary library = new();
        library.AddBook();
        library.SetPublicationMetadata(
            1,
            publisher: "Example Publisher",
            publicationDate: "2020-05-06 00:00:00+00:00",
            series: "Example Series",
            seriesIndex: 2.5m,
            languages: ["eng", "deu"],
            hasCover: true);
        using ServiceProvider provider = TestServices.CreateProvider();
        ICalibreMetadataReader reader = provider.GetRequiredService<ICalibreMetadataReader>();

        CalibreCatalogReadOutcome outcome = await reader.ReadAsync(
            new(library.RootPath, library.DatabasePath),
            null,
            CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Catalog!.LibraryUuid.Should().Be(library.LibraryUuid);
        outcome.Catalog.SchemaVersion.Should().Be(27);
        outcome.Catalog.Books.Should().ContainSingle();
        CalibreBookRecord book = outcome.Catalog.Books[0];
        book.Title.Should().Be("Book");
        book.AuthorSort.Should().Be("Author");
        book.Authors.Select(author => author.Name).Should().Equal("Second Author", "First Author");
        book.Authors.Select(author => author.SortName).Should().Equal("Author, Second", "Author, First");
        book.Identifiers.Select(identifier => identifier.Type).Should().Equal("asin", "isbn");
        book.Formats.Should().ContainSingle(format => format.Format == "EPUB" && format.StoredName == "Book");
        book.Publication.Should().NotBeNull();
        book.Publication!.Publisher.Should().Be("Example Publisher");
        book.Publication.PublicationDate!.Value.Year.Should().Be(2020);
        book.Publication.Series.Should().Be("Example Series");
        book.Publication.SeriesIndex.Should().Be(2.5m);
        book.Publication.Languages.Should().Equal("eng", "deu");
        book.Publication.HasCover.Should().BeTrue();
    }

    [Fact]
    public async Task ReadRejectsUnsupportedSchemaVersion()
    {
        using SyntheticCalibreLibrary library = new(schemaVersion: 26);
        using ServiceProvider provider = TestServices.CreateProvider();
        ICalibreMetadataReader reader = provider.GetRequiredService<ICalibreMetadataReader>();

        CalibreCatalogReadOutcome outcome = await reader.ReadAsync(
            new(library.RootPath, library.DatabasePath),
            null,
            CancellationToken.None);

        outcome.Error!.Code.Should().Be(LibraryErrorCode.UnsupportedSchema);
        outcome.Error.Message.Should().Be(
            "The Calibre library schema is not supported (schema 26; expected 27).");
    }

    [Fact]
    public async Task ReadRejectsMissingRequiredTable()
    {
        using SyntheticCalibreLibrary library = new();
        library.DropRequiredTable("identifiers");
        using ServiceProvider provider = TestServices.CreateProvider();
        ICalibreMetadataReader reader = provider.GetRequiredService<ICalibreMetadataReader>();

        CalibreCatalogReadOutcome outcome = await reader.ReadAsync(
            new(library.RootPath, library.DatabasePath),
            null,
            CancellationToken.None);

        outcome.Error!.Code.Should().Be(LibraryErrorCode.UnsupportedSchema);
    }

    [Fact]
    public async Task ReadRejectsNonSqliteDatabase()
    {
        using TemporaryDirectory directory = new();
        string database = Path.Combine(directory.Path, "metadata.db");
        await File.WriteAllTextAsync(database, "not a database");
        using ServiceProvider provider = TestServices.CreateProvider();
        ICalibreMetadataReader reader = provider.GetRequiredService<ICalibreMetadataReader>();

        CalibreCatalogReadOutcome outcome = await reader.ReadAsync(
            new(directory.Path, database),
            null,
            CancellationToken.None);

        outcome.Error!.Code.Should().BeOneOf(
            LibraryErrorCode.NotSqliteDatabase,
            LibraryErrorCode.CorruptDatabase);
    }
}
