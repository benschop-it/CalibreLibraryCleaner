using System.Collections.Specialized;
using System.IO;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task PickerCancellationLeavesSelectionUnchanged()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns(null);
        MainWindowViewModel viewModel = CreateViewModel(picker, out _, out _, out _);

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);

        viewModel.SelectedLibraryPath.Should().BeEmpty();
    }

    [Fact]
    public async Task SuccessfulScanDisplaysBooksAndMissingFormats()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        MainWindowViewModel viewModel = CreateViewModel(
            picker,
            out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader,
            out IFormatFileHasher hasher);
        ValidatedLibraryLocation location = new("library", "database");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateCatalog()));
        A.CallTo(() => resolver.ResolveFormat(
                location,
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull()))
            .Returns(ResolvedFormatPathOutcome.Success(new("library", "full", "Book/Book.epub")));
        A.CallTo(() => hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .Returns(Task.FromResult<IReadOnlyList<FormatHashResult>>(
                [FormatHashResult.Failure(0, FormatHashResultStatus.Missing, "FileNotFound")]));

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.Books.Should().ContainSingle();
        viewModel.SelectedFormats.Should().ContainSingle(format => format.Status == "Missing");
        viewModel.StatusMessage.Should().Contain("1 missing format files");
        viewModel.ExactDuplicateSummary.Should().Contain("No exact file duplicate groups");
    }

    [Fact]
    public async Task CancellationShowsNeutralStateAndAllowsRetry()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        MainWindowViewModel viewModel = CreateViewModel(
            picker,
            out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader,
            out _);
        ValidatedLibraryLocation location = new("library", "database");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .ReturnsLazily(call => WaitForCancellation(call.GetArgument<CancellationToken>(2)));
        await viewModel.SelectLibraryCommand.ExecuteAsync(null);

        Task scan = viewModel.ScanCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => viewModel.IsBusy);
        viewModel.CancelCommand.Execute(null);
        await scan;

        viewModel.StatusMessage.Should().Contain("canceled");
        viewModel.ErrorMessage.Should().BeEmpty();
        viewModel.IsBusy.Should().BeFalse();
        viewModel.ScanCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task SuccessfulScanDisplaysExactFileGroupMembers()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        MainWindowViewModel viewModel = CreateViewModel(
            picker,
            out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader,
            out IFormatFileHasher hasher);
        ValidatedLibraryLocation location = new("library", "database");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateCatalog(bookCount: 2)));
        A.CallTo(() => resolver.ResolveFormat(
                location,
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull()))
            .ReturnsLazily(call =>
            {
                string directory = call.GetArgument<string>(1)!;
                return ResolvedFormatPathOutcome.Success(new("library", directory, $"{directory}/Book.epub"));
            });
        FormatFileFingerprint fingerprint = new(4, new Sha256Digest(new string('d', 64)));
        A.CallTo(() => hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<IReadOnlyList<FormatHashResult>>(
                call.GetArgument<IReadOnlyList<FormatHashRequest>>(0)!
                    .Select(request => FormatHashResult.Success(request.Sequence, fingerprint))
                    .ToArray()));

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.ExactDuplicateGroups.Should().ContainSingle();
        viewModel.SelectedExactDuplicateMembers.Should().HaveCount(2);
        viewModel.ExactDuplicateGroups[0].RecordCount.Should().Be(2);
        viewModel.ExactDuplicateSummary.Should().Contain("1 exact file duplicate group");
    }

    [Fact]
    public async Task MetadataGroupsSupportFilteringNavigationAndSessionDeferWithoutIntegrationCalls()
    {
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns("library");
        MainWindowViewModel viewModel = CreateViewModel(
            picker,
            out ILibraryPathResolver resolver,
            out ICalibreMetadataReader reader,
            out IFormatFileHasher hasher);
        ValidatedLibraryLocation location = new("library", "database");
        A.CallTo(() => resolver.ValidateAsync("library", A<CancellationToken>._))
            .Returns(LibraryValidationOutcome.Success(location));
        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateMetadataDuplicateCatalog()));
        A.CallTo(() => resolver.ResolveFormat(
                location,
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull(),
                A<string>.That.IsNotNull()))
            .ReturnsLazily(call =>
            {
                string directory = call.GetArgument<string>(1)!;
                return ResolvedFormatPathOutcome.Success(new("library", directory, $"{directory}/Book.epub"));
            });
        A.CallTo(() => hasher.HashAsync(
                A<IReadOnlyList<FormatHashRequest>>._,
                A<int>._,
                A<IProgress<FormatHashProgress>?>._,
                A<CancellationToken>._))
            .ReturnsLazily(call => Task.FromResult<IReadOnlyList<FormatHashResult>>(
                call.GetArgument<IReadOnlyList<FormatHashRequest>>(0)!
                    .Select(request => FormatHashResult.Success(
                        request.Sequence,
                        new FormatFileFingerprint(
                            request.Sequence + 1,
                            new Sha256Digest(new string((char)('a' + request.Sequence), 64)))))
                    .ToArray()));

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.MetadataDuplicateGroups.Should().HaveCount(2);
        viewModel.StatusMessage.Should().Contain("2 exact metadata candidate groups");
        viewModel.MetadataDuplicateSummary.Should().Contain("2 of 2 metadata candidate groups visible");
        viewModel.SelectedMetadataDuplicateGroup!.NormalizedTitle.Should().Be("ALPHA:BOOK");
        viewModel.NextMetadataDuplicateGroupCommand.Execute(null);
        viewModel.SelectedMetadataDuplicateGroup.NormalizedTitle.Should().Be("BETA BOOK");
        viewModel.PreviousMetadataDuplicateGroupCommand.Execute(null);
        viewModel.SelectedMetadataDuplicateGroup.NormalizedTitle.Should().Be("ALPHA:BOOK");

        Fake.ClearRecordedCalls(resolver);
        Fake.ClearRecordedCalls(reader);
        Fake.ClearRecordedCalls(hasher);
        viewModel.MetadataDuplicateFilterText = "beta";
        viewModel.MetadataDuplicateGroups.Should().ContainSingle();
        viewModel.SelectedMetadataDuplicateGroup!.NormalizedTitle.Should().Be("BETA BOOK");
        viewModel.ToggleMetadataDuplicateDeferredCommand.Execute(null);
        viewModel.SelectedMetadataDuplicateGroup!.IsDeferred.Should().BeTrue();
        viewModel.MetadataDuplicateFilterMode = MetadataDuplicateFilterMode.Active;
        viewModel.MetadataDuplicateGroups.Should().BeEmpty();
        viewModel.MetadataDuplicateFilterMode = MetadataDuplicateFilterMode.Deferred;
        viewModel.MetadataDuplicateGroups.Should().ContainSingle(group => group.IsDeferred);
        Fake.GetCalls(resolver).Should().BeEmpty();
        Fake.GetCalls(reader).Should().BeEmpty();
        Fake.GetCalls(hasher).Should().BeEmpty();

        viewModel.MetadataDuplicateFilterText = string.Empty;
        viewModel.MetadataDuplicateFilterMode = MetadataDuplicateFilterMode.All;
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.MetadataDuplicateGroups.Should().ContainSingle(
            group => group.NormalizedTitle == "BETA BOOK" && group.IsDeferred);
        viewModel.MetadataDuplicateGroups.Should().ContainSingle(
            group => group.NormalizedTitle == "ALPHA:BOOK" && !group.IsDeferred);
        viewModel.SelectedMetadataDuplicateMembers.Should().HaveCount(2);
        viewModel.MetadataDuplicateGroups[0].Reason.Should().Contain("exactly equal");

        A.CallTo(() => reader.ReadAsync(location, A<IProgress<LibraryScanProgress>?>._, A<CancellationToken>._))
            .Returns(CalibreCatalogReadOutcome.Success(CreateMetadataDuplicateCatalog("different-library-uuid")));
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.MetadataDuplicateGroups.Should().OnlyContain(group => !group.IsDeferred);
    }

    [Fact]
    public async Task SyntheticLibraryFlowsThroughRealInfrastructureToDuplicateView()
    {
        using SyntheticDuplicateLibrary library = new();
        ILibraryFolderPicker picker = A.Fake<ILibraryFolderPicker>();
        A.CallTo(() => picker.PickFolder(A<string?>._)).Returns(library.RootPath);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        services.AddSingleton(new LibraryAnalysisOptions(maxHashConcurrency: 2));
        using ServiceProvider provider = services.BuildServiceProvider();
        MainWindowViewModel viewModel = new(
            new ValidateLibraryUseCase(provider.GetRequiredService<ILibraryPathResolver>()),
            new ScanLibraryUseCase(
                provider.GetRequiredService<ILibraryPathResolver>(),
                provider.GetRequiredService<ICalibreMetadataReader>(),
                provider.GetRequiredService<IFormatFileHasher>(),
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<LibraryAnalysisOptions>()),
            picker);
        int bookCollectionChanges = 0;
        int groupCollectionChanges = 0;
        ((INotifyCollectionChanged)viewModel.Books).CollectionChanged += (_, _) => bookCollectionChanges++;
        ((INotifyCollectionChanged)viewModel.ExactDuplicateGroups).CollectionChanged += (_, _) => groupCollectionChanges++;

        await viewModel.SelectLibraryCommand.ExecuteAsync(null);
        await viewModel.ScanCommand.ExecuteAsync(null);

        viewModel.ErrorMessage.Should().BeEmpty();
        viewModel.Books.Should().HaveCount(2);
        viewModel.ExactDuplicateGroups.Should().ContainSingle();
        viewModel.SelectedExactDuplicateMembers.Should().HaveCount(2);
        bookCollectionChanges.Should().Be(1);
        groupCollectionChanges.Should().Be(1);
    }

    private static MainWindowViewModel CreateViewModel(
        ILibraryFolderPicker picker,
        out ILibraryPathResolver resolver,
        out ICalibreMetadataReader reader,
        out IFormatFileHasher hasher)
    {
        resolver = A.Fake<ILibraryPathResolver>();
        reader = A.Fake<ICalibreMetadataReader>();
        IClock clock = A.Fake<IClock>();
        hasher = A.Fake<IFormatFileHasher>();
        return new(
            new ValidateLibraryUseCase(resolver),
            new ScanLibraryUseCase(resolver, reader, hasher, clock, new()),
            picker);
    }

    private static CalibreCatalogRecord CreateCatalog(int bookCount = 1) => new(
        "87f7ed1f-59a8-45a6-975a-7e06fd84780d",
        27,
        Enumerable.Range(1, bookCount).Select(id =>
            new CalibreBookRecord(
                id,
                $"Book {id}",
                "Author",
                $"Book {id}",
                [new CalibreAuthorRecord(id, "Author", "Author")],
                [],
            [new CalibreFormatRecord("EPUB", "Book")])));

    private static CalibreCatalogRecord CreateMetadataDuplicateCatalog(
        string libraryUuid = "87f7ed1f-59a8-45a6-975a-7e06fd84780d") => new(
        libraryUuid,
        27,
        new[]
        {
            (Id: 1, Title: "Alpha : Book", Author: "Alice"),
            (Id: 2, Title: "alpha:book", Author: "Alice"),
            (Id: 3, Title: "Beta Book", Author: "Bob"),
            (Id: 4, Title: "BETA  BOOK", Author: "Bob"),
        }.Select(item => new CalibreBookRecord(
            item.Id,
            item.Title,
            $"{item.Author}, Sort",
            $"Book {item.Id}",
            [new CalibreAuthorRecord(item.Id, item.Author, $"{item.Author}, Sort")],
            [new CalibreIdentifierRecord("isbn", $"context-{item.Id}")],
            [new CalibreFormatRecord("EPUB", "Book")])));

    private static async Task<CalibreCatalogReadOutcome> WaitForCancellation(
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Cancellation was expected.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class SyntheticDuplicateLibrary : IDisposable
    {
        public SyntheticDuplicateLibrary()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"CalibreLibraryCleaner-Wpf-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
            string databasePath = Path.Combine(RootPath, "metadata.db");
            using SqliteConnection connection = new($"Data Source={databasePath};Mode=ReadWriteCreate;Pooling=False");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA user_version=27;
                CREATE TABLE library_id (id INTEGER PRIMARY KEY, uuid TEXT NOT NULL UNIQUE);
                CREATE TABLE books (id INTEGER PRIMARY KEY, title TEXT NOT NULL, author_sort TEXT, path TEXT NOT NULL);
                CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT NOT NULL, sort TEXT);
                CREATE TABLE books_authors_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, author INTEGER NOT NULL, UNIQUE(book, author));
                CREATE TABLE identifiers (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, type TEXT NOT NULL COLLATE NOCASE, val TEXT NOT NULL COLLATE NOCASE, UNIQUE(book, type));
                CREATE TABLE data (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, format TEXT NOT NULL COLLATE NOCASE, name TEXT NOT NULL, UNIQUE(book, format));
                INSERT INTO library_id(id, uuid) VALUES (1, '87f7ed1f-59a8-45a6-975a-7e06fd84780d');
                INSERT INTO books(id, title, author_sort, path) VALUES (1, 'Book 1', 'Author 1', 'Author 1/Book (1)');
                INSERT INTO books(id, title, author_sort, path) VALUES (2, 'Book 2', 'Author 2', 'Author 2/Book (2)');
                INSERT INTO authors(id, name, sort) VALUES (1, 'Author 1', 'Author 1');
                INSERT INTO authors(id, name, sort) VALUES (2, 'Author 2', 'Author 2');
                INSERT INTO books_authors_link(id, book, author) VALUES (1, 1, 1);
                INSERT INTO books_authors_link(id, book, author) VALUES (2, 2, 2);
                INSERT INTO data(id, book, format, name) VALUES (1, 1, 'EPUB', 'Book 1');
                INSERT INTO data(id, book, format, name) VALUES (2, 2, 'EPUB', 'Book 2');
                """;
            command.ExecuteNonQuery();

            byte[] content = [1, 2, 3, 4];
            WriteFormat("Author 1", "Book (1)", "Book 1.epub", content);
            WriteFormat("Author 2", "Book (2)", "Book 2.epub", content);
        }

        public string RootPath { get; }

        public void Dispose() => Directory.Delete(RootPath, recursive: true);

        private void WriteFormat(string author, string book, string fileName, byte[] content)
        {
            string directory = Path.Combine(RootPath, author, book);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, fileName), content);
        }
    }
}
