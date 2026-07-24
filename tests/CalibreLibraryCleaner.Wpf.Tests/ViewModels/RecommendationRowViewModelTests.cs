using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class RecommendationRowViewModelTests
{
    [Fact]
    public void RowDisplaysGeneratedEvidenceAndKeepsReviewedStateSeparate()
    {
        FormatFileFingerprint fingerprint = new(10, new(new string('a', 64)));
        CalibreBook first = Book(1, [Present("PDF", "one.pdf", fingerprint)], new(hasCover: true, languages: ["eng"]));
        CalibreBook second = Book(2, [], new(languages: ["eng"]));
        CalibreBook[] books = [first, second];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "library"),
            group,
            books,
            [],
            [],
            [],
            CancellationToken.None);

        MetadataDuplicateGroupRowViewModel row = new(group, books.ToDictionary(value => value.Id), generated);

        row.Confidence.Should().Be(generated.Confidence.ToString());
        row.ReviewStatus.Should().Be("Unreviewed");
        row.FormatRows.Should().ContainSingle(value => value.GeneratedSource == "Record 1");
        row.FormatRows.Single().SourceOptions.Should().Contain(value => value.Label.Contains("preserve all", StringComparison.Ordinal));
        row.ReasonRows.Should().NotBeEmpty();
        row.WarningRows.Should().Contain(value => value.Code == "FORMAT.NO_DECLARED_FORMATS");
        row.Members.Should().OnlyContain(value => value.MetadataQualityFacts != "Not ranked");
        row.Recommendation.Should().BeSameAs(generated);
    }

    [Fact]
    public void RowDisplaysStaleOverrideChoicesWithoutApplyingThem()
    {
        FormatFileFingerprint fingerprint = new(10, new(new string('a', 64)));
        CalibreBook[] books = [
            Book(1, [Present("PDF", "one.pdf", fingerprint)], new(languages: ["eng"])),
            Book(2, [], new(languages: ["eng"])),
        ];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "library"), group, books, [], [], [], CancellationToken.None);
        UserRecommendationOverride proposed = new(
            generated.ModelVersion,
            generated.InputVersion,
            RecommendationReviewStatus.ManuallyAdjusted,
            DateTimeOffset.UnixEpoch,
            formatOverrides: [new("PDF", FormatOverrideAction.ExcludeFinalFormat)]);
        ReviewedConsolidationRecommendation previous = ApplyRecommendationOverrideUseCase.Execute(generated, proposed).Reviewed!;
        ConsolidationRecommendation changed = new(
            generated.GroupId, generated.MemberIds, generated.ModelVersion, new("changed"), generated.MetadataSource,
            generated.FormatSelections, generated.RecordRecommendations, generated.Reasons, generated.Warnings, generated.Confidence);
        MetadataDuplicateGroupRowViewModel row = new(group, books.ToDictionary(value => value.Id), changed);

        row.SetReviewed(RecommendationReviewStalenessEvaluator.Reconcile(changed, previous));

        row.Freshness.Should().Be("Stale");
        row.StaleOverrideSummary.Should().Contain("PDF: ExcludeFinalFormat");
        row.FormatRows.Single().ReviewedSource!.Action.Should().NotBe("ExcludeFinalFormat");
    }

    [Fact]
    public void FullySeparatedGroupDisplaysEveryRecordAsRetainedAndNoMetadataSource()
    {
        CalibreBook[] books = [
            Book(1, [], new(languages: ["eng"])),
            Book(2, [], new(languages: ["deu"])),
        ];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "library"), group, books, [], [], [], CancellationToken.None);

        MetadataDuplicateGroupRowViewModel row = new(group, books.ToDictionary(value => value.Id), generated);

        row.Members.Should().OnlyContain(value => value.IsRetainedSeparate);
        row.ReviewedMetadataSource.Should().BeNull();
    }

    [Fact]
    public void UnavailableFormatIsDistinctFromUnresolvedReviewAction()
    {
        CalibreBook[] books = [
            Book(1, [new("PDF", "missing", "missing.pdf", FormatFileStatus.Missing)], new(languages: ["eng"])),
            Book(2, [], new(languages: ["eng"])),
        ];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "library"), group, books, [], [], [], CancellationToken.None);

        MetadataDuplicateGroupRowViewModel row = new(group, books.ToDictionary(value => value.Id), generated);

        row.FormatRows.Single().ReviewedSource!.Action.Should().Be("Unavailable");
        row.FormatRows.Single().SourceOptions.Should().Contain(value => value.Action == "MarkUnresolved");
    }

    private static CalibreBook Book(long id, BookFormat[] formats, BookPublicationMetadata metadata) => new(
        new(id),
        "Shared",
        "Author",
        [new(new(id), "Author", "Author")],
        [],
        formats,
        $"Book ({id})",
        metadata);

    private static BookFormat Present(string format, string path, FormatFileFingerprint fingerprint) => new(
        format,
        "book",
        path,
        FormatFileStatus.Present,
        fingerprint,
        new FormatFileObservation(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0));
}
