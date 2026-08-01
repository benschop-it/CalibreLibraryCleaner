using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;
using CalibreLibraryCleaner.Infrastructure.Tests.Execution;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using CalibreLibraryCleaner.Infrastructure.Tests.Pdf;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.LibrarySnapshots;

public sealed class LibrarySnapshotJsonSerializerTests
{
    [Fact]
    public void CompleteSnapshotRoundTripsCanonically()
    {
        using Fixtures.TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);

        byte[] serialized = LibrarySnapshotJsonSerializer.Serialize(fixture.Snapshot);
        LibrarySnapshotJsonReadResult read = LibrarySnapshotJsonSerializer.Deserialize(serialized);

        read.IsSuccess.Should().BeTrue(read.Error);
        read.Snapshot!.Identity.Should().Be(fixture.Snapshot.Identity);
        read.Snapshot.ScannedAt.Should().Be(fixture.Snapshot.ScannedAt);
        read.Snapshot.Books.Should().BeEquivalentTo(fixture.Snapshot.Books);
        read.Snapshot.ExactMetadataDuplicateGroups.Should().BeEquivalentTo(fixture.Snapshot.ExactMetadataDuplicateGroups);
        read.Snapshot.ConsolidationRecommendations.Should().BeEquivalentTo(fixture.Snapshot.ConsolidationRecommendations);
        LibrarySnapshotJsonSerializer.Serialize(read.Snapshot).Should().Equal(serialized);
    }

    [Fact]
    public async Task EpubAndPdfAssessmentsRoundTripCanonically()
    {
        using TemporaryDirectory fixtureDirectory = new();
        string epubPath = Path.Combine(fixtureDirectory.Path, "Valid.epub");
        SyntheticEpubBuilder.CreateValid(epubPath);
        using SyntheticPdfFixture pdf = SyntheticPdfFixture.CreateText(text: "Persisted PDF assessment text.");
        using SyntheticCalibreLibrary library = new();
        library.AddSimpleBook(1, await File.ReadAllBytesAsync(epubPath));
        library.AddSimpleBook(2, await File.ReadAllBytesAsync(pdf.Path), "PDF");
        using ServiceProvider provider = TestServices.CreateProvider();
        LibraryScanOutcome scan = await TestServices.CreateScanUseCase(provider)
            .ExecuteAsync(library.RootPath, null, CancellationToken.None);

        byte[] serialized = LibrarySnapshotJsonSerializer.Serialize(scan.Snapshot!);
        LibrarySnapshotJsonReadResult read = LibrarySnapshotJsonSerializer.Deserialize(serialized);

        read.IsSuccess.Should().BeTrue(read.Error);
        read.Snapshot!.EpubAssessments.Should().ContainSingle();
        read.Snapshot.PdfAssessments.Should().ContainSingle();
        LibrarySnapshotJsonSerializer.Serialize(read.Snapshot).Should().Equal(serialized);
    }
}
