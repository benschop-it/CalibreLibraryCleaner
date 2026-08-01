using System.Security.Cryptography;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Execution;

public sealed class ExactBinaryRecordBackupStoreTests
{
    [Fact]
    public async Task CompleteRecordExportsAndRawFormatsSealAndReverifyOutsideLibrary()
    {
        using TemporaryDirectory temporary = new();
        (ExactBinaryCleanupPlan plan, string libraryRoot, byte[] bytes) = Plan(temporary.Path);
        string backupParent = Path.Combine(temporary.Path, "external");
        Directory.CreateDirectory(backupParent);
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCalibreLibraryInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();
        IExecutionBackupStore workspaceStore = provider.GetRequiredService<IExecutionBackupStore>();
        IExactBinaryRecordBackupStore store = provider.GetRequiredService<IExactBinaryRecordBackupStore>();
        CleanupExecutionId executionId = new(Guid.Parse("11111111-2222-3333-4444-555555555555"));
        ExecutionWorkspace workspace = await workspaceStore.CreateWorkspaceAsync(
            executionId, backupParent, CancellationToken.None);
        CalibreToolDescriptor tool = new("C:\\Calibre2\\calibredb.exe",
            new("C:\\Calibre2\\calibredb.exe", "9.11.0", new(new string('f', 64)),
                "calibredb/windows/9.11.0"), Enum.GetValues<CalibreExecutionCapability>());

        ExactBinaryRecordBackupInputs inputs = await store.CreateInputsAsync(
            workspace, plan, libraryRoot, tool, "test", CancellationToken.None);
        foreach (string exportDirectory in inputs.ExportDirectories.Values)
        {
            File.WriteAllText(Path.Combine(exportDirectory, "metadata.opf"), "<package><metadata/></package>");
            File.WriteAllBytes(Path.Combine(exportDirectory, "book.epub"), bytes);
        }
        ExactBinaryRecordBackupResult result = await store.VerifyAndSealAsync(
            inputs, plan, CancellationToken.None);

        inputs.Issues.Should().BeEmpty();
        result.IsSuccess.Should().BeTrue();
        result.Manifest!.Entries.Should().Contain(value => value.RelativePath == "approved.exact-binary-plan.json");
        result.Manifest.Entries.Count(value => value.RelativePath.StartsWith("raw-formats/", StringComparison.Ordinal))
            .Should().Be(2);
        (await store.VerifyAvailableAsync(workspace, result.Manifest, CancellationToken.None)).Should().BeEmpty();
    }

    private static (ExactBinaryCleanupPlan Plan, string LibraryRoot, byte[] Bytes) Plan(string root)
    {
        string library = Path.Combine(root, "library");
        Directory.CreateDirectory(library);
        File.WriteAllBytes(Path.Combine(library, "metadata.db"), [0]);
        byte[] bytes = "identical"u8.ToArray();
        FormatFileFingerprint fingerprint = new(bytes.Length,
            new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
        CalibreBook first = CreateBook(1, "Alice", library, bytes, fingerprint);
        CalibreBook second = CreateBook(2, "Bob", library, bytes, fingerprint);
        CalibreBook[] books = [first, second];
        LibrarySnapshot snapshot = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, library),
            DateTimeOffset.UtcNow, books, [], ExactBinaryDuplicateDetector.Detect(books));
        ExactBinaryDuplicateGroup group = snapshot.ExactBinaryDuplicateGroups.Single();
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        A.CallTo(() => ids.Create()).Returns(new CleanupPlanId(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(DateTimeOffset.UtcNow);
        ExactBinaryCleanupPlan valid = new GenerateExactBinaryCleanupPlanUseCase(ids, clock).Execute(
            snapshot, group.Id, group.Members.Single(value => value.BookId == first.Id), [second.Id]).Plan!;
        ExactBinaryCleanupPlan approved = new ApproveExactBinaryCleanupPlanUseCase(clock).Execute(valid, snapshot).Plan!;
        return (approved, library, bytes);
    }

    private static CalibreBook CreateBook(
        long id,
        string author,
        string library,
        byte[] bytes,
        FormatFileFingerprint fingerprint)
    {
        string relativeDirectory = $"{author}/Book ({id})";
        string directory = Path.Combine(library, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "book.epub");
        File.WriteAllBytes(path, bytes);
        FileInfo info = new(path); info.Refresh();
        BookFormat format = new("EPUB", "book", $"{relativeDirectory}/book.epub",
            FormatFileStatus.Present, fingerprint,
            new(info.Length, new(info.CreationTimeUtc, TimeSpan.Zero),
                new(info.LastWriteTimeUtc, TimeSpan.Zero), (int)info.Attributes));
        return new(new(id), $"Book {id}", author, [new(new(id), author, author)], [], [format], relativeDirectory);
    }
}
