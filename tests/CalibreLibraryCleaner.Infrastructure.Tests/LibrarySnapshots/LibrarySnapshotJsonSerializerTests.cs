using System.IO.Compression;
using System.Text;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Infrastructure.LibrarySnapshots;
using CalibreLibraryCleaner.Infrastructure.Tests.Execution;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using CalibreLibraryCleaner.Infrastructure.Tests.Pdf;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
    public void ExactOnlySnapshotWithoutMatchingFieldsLoadsAsUnavailable()
    {
        using Fixtures.TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        JObject document = JObject.Parse(Encoding.UTF8.GetString(
            LibrarySnapshotJsonSerializer.Serialize(fixture.Snapshot)));
        JObject snapshot = (JObject)document["snapshot"]!;
        snapshot.Remove("workLanguageCandidateGroups");
        snapshot.Remove("matchingRunSummary");
        byte[] legacy = Encoding.UTF8.GetBytes(document.ToString(Formatting.Indented) + "\n");

        LibrarySnapshotJsonReadResult read = LibrarySnapshotJsonSerializer.Deserialize(legacy);

        read.IsSuccess.Should().BeTrue(read.Error);
        read.Snapshot!.WorkLanguageCandidateGroups.Should().BeEmpty();
        read.Snapshot.MatchingRunSummary.Status.Should().Be(MatchingEvidenceStatus.Unavailable);
        read.Snapshot.MatchingRunSummary.RecordCount.Should().Be(fixture.Snapshot.Books.Count);
    }

    [Fact]
    public void InferredMatchingEvidenceRoundTripsCanonically()
    {
        using Fixtures.TemporaryDirectory directory = new();
        InfrastructureExecutionFixture fixture = InfrastructureExecutionTestData.Create(directory.Path);
        CalibreBookId[] members = fixture.Snapshot.Books.Select(value => value.Id).ToArray();
        WorkLanguageCandidateGroup group = WorkLanguageCandidateGroup.Create(
            "nl",
            members,
            [members[0]],
            WorkLanguageCandidateConfidence.Strong,
            [
                new("MATCH.CONTENT.HIGH_SIMILARITY", CandidateEvidenceStrength.Anchor),
                new("MATCH.AUTHOR.COMPATIBLE", CandidateEvidenceStrength.Supporting),
            ],
            contentComparison: new(1, 0, 1, 0, 0, 0));
        BookMatchingRunSummary summary = new(
            MatchingPolicyVersion.V1,
            MatchingEvidenceStatus.Available,
            fixture.Snapshot.Books.Count,
            1,
            1,
            0,
            2,
            1,
            1,
            1,
            0);
        LibrarySnapshot value = new(
            fixture.Snapshot.Identity,
            fixture.Snapshot.ScannedAt,
            fixture.Snapshot.Books,
            fixture.Snapshot.Findings,
            fixture.Snapshot.ExactBinaryDuplicateGroups,
            fixture.Snapshot.ExactMetadataDuplicateGroups,
            fixture.Snapshot.EpubAssessments,
            fixture.Snapshot.ConsolidationRecommendations,
            fixture.Snapshot.PdfAssessments,
            [group],
            summary);

        byte[] serialized = LibrarySnapshotJsonSerializer.Serialize(value);
        LibrarySnapshotJsonReadResult read = LibrarySnapshotJsonSerializer.Deserialize(serialized);

        read.IsSuccess.Should().BeTrue(read.Error);
        read.Snapshot!.WorkLanguageCandidateGroups.Should().ContainSingle().Which
            .Should().BeEquivalentTo(group);
        read.Snapshot.WorkLanguageCandidateGroups[0].CleanupEligibility
            .Should().Be(WorkLanguageCleanupEligibility.ExplicitKeeperCleanup);
        read.Snapshot.MatchingRunSummary.Should().BeEquivalentTo(summary);
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

    [Fact]
    public async Task FallbackReadableAssessmentRoundTripPreservesCoverageEvidenceAndScoreCap()
    {
        using TemporaryDirectory fixtureDirectory = new();
        string epubPath = Path.Combine(fixtureDirectory.Path, "Fallback.epub");
        SyntheticEpubBuilder.CreateFromEntries(
            epubPath,
            [("chapter.xhtml", $"<html><body><p>{new string('a', 6_000)}</p></body></html>", CompressionLevel.Optimal)]);
        using SyntheticCalibreLibrary library = new();
        library.AddSimpleBook(1, await File.ReadAllBytesAsync(epubPath));
        using ServiceProvider provider = TestServices.CreateProvider();
        LibraryScanOutcome scan = await TestServices.CreateScanUseCase(provider)
            .ExecuteAsync(library.RootPath, null, CancellationToken.None);

        EpubAssessment original = scan.Snapshot!.EpubAssessments.Should().ContainSingle().Subject;
        byte[] serialized = LibrarySnapshotJsonSerializer.Serialize(scan.Snapshot);
        LibrarySnapshotJsonReadResult read = LibrarySnapshotJsonSerializer.Deserialize(serialized);

        read.IsSuccess.Should().BeTrue(read.Error);
        EpubAssessment roundTripped = read.Snapshot!.EpubAssessments.Should().ContainSingle().Subject;
        roundTripped.ScoreCap.Should().Be(70);
        roundTripped.UncappedScore.Should().Be(original.UncappedScore);
        roundTripped.Features.Coverage.Should().Be(EpubAssessmentCoverage.FallbackReadable);
        roundTripped.Features.RenderableEvidence.Should().HaveFlag(EpubRenderableEvidence.Text);
        LibrarySnapshotJsonSerializer.Serialize(read.Snapshot).Should().Equal(serialized);

        string malformedJson = Encoding.UTF8.GetString(serialized)
            .Replace("\"scoreCap\": 70", "\"scoreCap\": null", StringComparison.Ordinal);
        malformedJson.Should().NotBe(Encoding.UTF8.GetString(serialized));
        LibrarySnapshotJsonReadResult malformed = LibrarySnapshotJsonSerializer.Deserialize(Encoding.UTF8.GetBytes(malformedJson));
        malformed.IsSuccess.Should().BeFalse();
    }
}
