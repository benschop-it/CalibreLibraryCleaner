using System.Text;
using System.Text.Json;
using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Infrastructure.Recommendations;
using CalibreLibraryCleaner.Infrastructure.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CalibreLibraryCleaner.Infrastructure.Tests.Recommendations;

public sealed class RecommendationJsonTests
{
    [Fact]
    public void SameInputsAndTimestampProduceByteIdenticalVersionedJson()
    {
        RecommendationReviewExportDocument document = Document();

        byte[] first = RecommendationJsonSerializer.Serialize(document);
        byte[] second = RecommendationJsonSerializer.Serialize(document);

        first.Should().Equal(second);
        first.Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);
        string json = Encoding.UTF8.GetString(first);
        json.Should().StartWith("{\n  \"schemaVersion\": \"recommendation-review/1.0\",\n  \"recommendationModelVersion\": \"consolidation-recommendation/1.0.2\"");
        json.Should().Contain("\"metadataDecision\"").And.Contain("\"recordDecisions\"");
        json.Should().NotContain("C:\\secret\\library");
        json.Should().NotContain("cleanupPlan").And.NotContain("commandArguments").And.NotContain("removals");
        RecommendationJsonSerializer.Inspect(first).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void UnknownSchemaAndMalformedJsonReturnControlledFailures()
    {
        byte[] bytes = RecommendationJsonSerializer.Serialize(Document());
        string changed = Encoding.UTF8.GetString(bytes).Replace("recommendation-review/1.0", "recommendation-review/9.0", StringComparison.Ordinal);

        RecommendationJsonSerializer.Inspect(Encoding.UTF8.GetBytes(changed)).Error!.Code.Should().Be("UNSUPPORTED_SCHEMA_VERSION");
        RecommendationJsonSerializer.Inspect("{"u8).Error!.Code.Should().Be("MALFORMED_JSON");
    }

    [Fact]
    public async Task ExportOutsideLibrarySucceedsWhileInsideLibraryIsRejectedWithoutSidecars()
    {
        using TemporaryDirectory library = new();
        using TemporaryDirectory outside = new();
        using ServiceProvider provider = TestServices.CreateProvider();
        IRecommendationExporter exporter = provider.GetRequiredService<IRecommendationExporter>();
        string outsidePath = Path.Combine(outside.Path, "review.json");
        string insidePath = Path.Combine(library.Path, "review.json");

        RecommendationExportWriteOutcome success = await exporter.ExportAsync(Document(), library.Path, outsidePath, CancellationToken.None);
        RecommendationExportWriteOutcome rejected = await exporter.ExportAsync(Document(), library.Path, insidePath, CancellationToken.None);

        success.IsSuccess.Should().BeTrue();
        File.Exists(outsidePath).Should().BeTrue();
        rejected.Error!.Code.Should().Be("DESTINATION_INSIDE_LIBRARY");
        Directory.EnumerateFileSystemEntries(library.Path).Should().BeEmpty();
        Directory.EnumerateFiles(outside.Path, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task PreCanceledExportPublishesNothing()
    {
        using TemporaryDirectory library = new();
        using TemporaryDirectory outside = new();
        using ServiceProvider provider = TestServices.CreateProvider();
        IRecommendationExporter exporter = provider.GetRequiredService<IRecommendationExporter>();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Func<Task> act = () => exporter.ExportAsync(Document(), library.Path, Path.Combine(outside.Path, "review.json"), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.EnumerateFileSystemEntries(outside.Path).Should().BeEmpty();
    }

    [Fact]
    public void SerializationRejectsRootedManagedPath()
    {
        RecommendationReviewExportDocument document = Document("C:\\secret\\book.pdf");

        Action act = () => RecommendationJsonSerializer.Serialize(document);

        act.Should().Throw<ArgumentException>().WithMessage("*safe relative paths*");
    }

    [Fact]
    public void MissingRelativePathAndInvalidEnumReturnControlledInspectionFailures()
    {
        string json = Encoding.UTF8.GetString(RecommendationJsonSerializer.Serialize(Document()));
        string missingPath = json.Replace("\"relativePath\"", "\"unexpectedPath\"", StringComparison.Ordinal);
        string invalidEnum = json.Replace("\"reviewStatus\": \"Unreviewed\"", "\"reviewStatus\": \"Invalid\"", StringComparison.Ordinal);

        RecommendationJsonSerializer.Inspect(Encoding.UTF8.GetBytes(missingPath)).IsSuccess.Should().BeFalse();
        RecommendationJsonSerializer.Inspect(Encoding.UTF8.GetBytes(invalidEnum)).Error!.Code.Should().Be("INVALID_RECOMMENDATION");
    }

    [Fact]
    public async Task PhysicalLibraryDestinationIsRejectedWhenSelectedRootIsAnAlias()
    {
        using TemporaryDirectory library = new();
        using TemporaryDirectory outside = new();
        string alias = Path.Combine(outside.Path, "library-alias");
        try
        {
            Directory.CreateSymbolicLink(alias, library.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        using ServiceProvider provider = TestServices.CreateProvider();
        IRecommendationExporter exporter = provider.GetRequiredService<IRecommendationExporter>();

        RecommendationExportWriteOutcome outcome = await exporter.ExportAsync(
            Document(),
            alias,
            Path.Combine(library.Path, "review.json"),
            CancellationToken.None);

        outcome.Error!.Code.Should().Be("DESTINATION_INSIDE_LIBRARY");
        Directory.EnumerateFileSystemEntries(library.Path).Should().BeEmpty();
    }

    [Fact]
    public void SerializationRejectsEffectiveSelectionThatDoesNotMatchTheValidatedReview()
    {
        RecommendationReviewExportDocument safe = Document();
        RecommendationReviewExportGroup group = safe.Groups.Single();
        ConsolidationRecommendation generated = group.Reviewed.Generated;
        BookFormat currentFormat = group.Members.Single(value => value.Id.Value == 1).Formats.Single();
        RecommendationFormatCandidate unsafeCandidate = new(
            new(1),
            "PDF",
            "C:\\secret\\leak.pdf",
            FormatFileStatus.Present,
            currentFormat.Fingerprint);
        FormatSourceSelection unsafeSelection = new(
            "PDF",
            [unsafeCandidate],
            unsafeCandidate,
            FormatResolutionStatus.Selected,
            [],
            RecommendationDecisionStrength.Ambiguous,
            ["USER.SELECTED_FORMAT_SOURCE"]);
        ReviewedConsolidationRecommendation forged = new(
            generated,
            null,
            new(generated.MetadataSource?.SelectedBookId, [unsafeSelection], []),
            RecommendationReviewStatus.Unreviewed,
            RecommendationFreshness.Current);
        RecommendationReviewExportDocument document = new(
            safe.SourceLibraryUuid,
            safe.SourceSchemaVersion,
            safe.ExportedAtUtc,
            [new(forged, group.Members)]);

        Action act = () => RecommendationJsonSerializer.Serialize(document);

        act.Should().Throw<ArgumentException>().WithMessage("*effective reviewed selection*");
    }

    [Fact]
    public async Task ExportFailureForInvalidEffectiveSelectionPublishesNoArtifactOrTemporaryFile()
    {
        RecommendationReviewExportDocument safe = Document();
        RecommendationReviewExportGroup group = safe.Groups.Single();
        ConsolidationRecommendation generated = group.Reviewed.Generated;
        BookFormat currentFormat = group.Members.Single(value => value.Id.Value == 1).Formats.Single();
        RecommendationFormatCandidate unsafeCandidate = new(
            new(1), "PDF", "C:\\secret\\leak.pdf", FormatFileStatus.Present, currentFormat.Fingerprint);
        FormatSourceSelection unsafeSelection = new(
            "PDF", [unsafeCandidate], unsafeCandidate, FormatResolutionStatus.Selected, [],
            RecommendationDecisionStrength.Ambiguous, ["USER.SELECTED_FORMAT_SOURCE"]);
        ReviewedConsolidationRecommendation forged = new(
            generated,
            null,
            new(generated.MetadataSource?.SelectedBookId, [unsafeSelection], []),
            RecommendationReviewStatus.Unreviewed,
            RecommendationFreshness.Current);
        RecommendationReviewExportDocument document = new(
            safe.SourceLibraryUuid, safe.SourceSchemaVersion, safe.ExportedAtUtc, [new(forged, group.Members)]);
        using TemporaryDirectory library = new();
        using TemporaryDirectory outside = new();
        using ServiceProvider provider = TestServices.CreateProvider();
        IRecommendationExporter exporter = provider.GetRequiredService<IRecommendationExporter>();
        string destination = Path.Combine(outside.Path, "review.json");

        RecommendationExportWriteOutcome outcome = await exporter.ExportAsync(
            document, library.Path, destination, CancellationToken.None);

        outcome.Error!.Code.Should().Be("EXPORT_WRITE_FAILED");
        Directory.EnumerateFileSystemEntries(outside.Path).Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(library.Path).Should().BeEmpty();
    }

    private static RecommendationReviewExportDocument Document(string relativePath = "Author/Book (1)/book.pdf")
    {
        FormatFileFingerprint fingerprint = new(5, new(new string('a', 64)));
        CalibreBook first = new(new(1), "Shared", "Author", [new(new(1), "Author", "Author")], [], [new BookFormat("PDF", "book", relativePath, FormatFileStatus.Present, fingerprint)], "Author/Book (1)");
        CalibreBook second = new(new(2), "Shared", "Author", [new(new(2), "Author", "Author")], [], [], "Author/Book (2)");
        CalibreBook[] books = [first, second];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\secret\\library"),
            group,
            books,
            [],
            [],
            [],
            CancellationToken.None);
        ReviewedConsolidationRecommendation reviewed = ApplyRecommendationOverrideUseCase.Reset(generated);
        return new(
            "87f7ed1f-59a8-45a6-975a-7e06fd84780d",
            27,
            DateTimeOffset.UnixEpoch,
            [new(reviewed, books)]);
    }
}
