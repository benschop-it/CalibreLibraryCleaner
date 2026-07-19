using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Recommendations;

public sealed class RecommendationUseCaseTests
{
    [Fact]
    public async Task GenerationPreservesCanonicalGroupOrderAndReportsCompletion()
    {
        LibrarySnapshot snapshot = Snapshot();
        List<RecommendationGenerationProgress> progress = [];
        GenerateConsolidationRecommendationsUseCase useCase = new(new());

        IReadOnlyList<ConsolidationRecommendation> result = await useCase.ExecuteAsync(
            snapshot,
            new InlineProgress<RecommendationGenerationProgress>(progress.Add),
            CancellationToken.None);

        result.Should().HaveCount(snapshot.ExactMetadataDuplicateGroups.Count);
        result.Select(value => value.GroupId).Should().Equal(snapshot.ExactMetadataDuplicateGroups.Select(value => value.Id));
        progress.Last().Should().Be(new RecommendationGenerationProgress(result.Count, result.Count));
    }

    [Fact]
    public async Task PreCanceledGenerationPublishesNoResult()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        GenerateConsolidationRecommendationsUseCase useCase = new(new());

        Func<Task> act = () => useCase.ExecuteAsync(Snapshot(), null, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ThousandsOfGroupsRetainDeterministicOrder()
    {
        CalibreBook[] books = Enumerable.Range(0, 1_000)
            .SelectMany(group => new[]
            {
                ScaleBook((group * 2) + 1, $"Book {group:D4}"),
                ScaleBook((group * 2) + 2, $"Book {group:D4}"),
            })
            .Reverse()
            .ToArray();
        IReadOnlyList<ExactMetadataDuplicateGroup> groups = ExactMetadataDuplicateDetector.Detect(books);
        LibrarySnapshot snapshot = new(new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "library"), DateTimeOffset.UnixEpoch, books, [], exactMetadataDuplicateGroups: groups);

        IReadOnlyList<ConsolidationRecommendation> result = await new GenerateConsolidationRecommendationsUseCase(new()).ExecuteAsync(snapshot, null, CancellationToken.None);

        result.Should().HaveCount(1_000);
        result.Select(value => value.GroupId).Should().Equal(groups.Select(value => value.Id));
    }

    [Fact]
    public void ExplicitFormatExclusionIsVisibleAndGeneratedRecommendationIsPreserved()
    {
        ConsolidationRecommendation generated = Generate();
        UserRecommendationOverride proposed = new(
            generated.ModelVersion,
            generated.InputVersion,
            RecommendationReviewStatus.ManuallyAdjusted,
            DateTimeOffset.UnixEpoch,
            formatOverrides: [new("PDF", FormatOverrideAction.ExcludeFinalFormat)]);

        RecommendationOverrideOutcome outcome = ApplyRecommendationOverrideUseCase.Execute(generated, proposed);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reviewed!.Generated.Should().BeSameAs(generated);
        outcome.Reviewed.EffectiveSelection!.FormatSelections.Single().ResolutionStatus.Should().Be(FormatResolutionStatus.ExplicitlyExcludedByUser);
        generated.FormatSelections.Single().ResolutionStatus.Should().Be(FormatResolutionStatus.Selected);
        outcome.Reviewed.ReviewStatus.Should().Be(RecommendationReviewStatus.ManuallyAdjusted);
    }

    [Fact]
    public void ChangedInputRetainsButDoesNotApplyStaleOverride()
    {
        ConsolidationRecommendation generated = Generate();
        UserRecommendationOverride proposed = new(generated.ModelVersion, generated.InputVersion, RecommendationReviewStatus.Deferred, DateTimeOffset.UnixEpoch);
        ReviewedConsolidationRecommendation previous = ApplyRecommendationOverrideUseCase.Execute(generated, proposed).Reviewed!;
        ConsolidationRecommendation changed = new(
            generated.GroupId,
            generated.MemberIds,
            generated.ModelVersion,
            new("changed-input"),
            generated.MetadataSource,
            generated.FormatSelections,
            generated.RecordRecommendations,
            generated.Reasons,
            generated.Warnings,
            generated.Confidence);

        ReviewedConsolidationRecommendation reconciled = RecommendationReviewStalenessEvaluator.Reconcile(changed, previous);

        reconciled.Freshness.Should().Be(RecommendationFreshness.Stale);
        reconciled.EffectiveSelection.Should().BeNull();
        reconciled.StaleOverride.Should().BeSameAs(proposed);
    }

    [Fact]
    public void AcceptingGeneratedRecommendationPreservesGeneratedSeparateRecords()
    {
        CalibreBook first = BookWithLanguage(1, "eng");
        CalibreBook second = BookWithLanguage(2, "deu");
        CalibreBook[] books = [first, second];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "library"), group, books, [], [], [], CancellationToken.None);
        UserRecommendationOverride accepted = new(generated.ModelVersion, generated.InputVersion, RecommendationReviewStatus.Accepted, DateTimeOffset.UnixEpoch);

        RecommendationOverrideOutcome outcome = ApplyRecommendationOverrideUseCase.Execute(generated, accepted);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reviewed!.Generated.RetainedSeparateRecords.Should().BeEquivalentTo(generated.RetainedSeparateRecords);
        outcome.Reviewed.EffectiveSelection.Should().BeNull("all records are retained separately, so there is no effective consolidated selection");
    }

    [Fact]
    public void ManualOverrideCannotClearGeneratedSeparateRecords()
    {
        CalibreBook first = BookWithLanguage(1, "eng");
        CalibreBook second = BookWithLanguage(2, "deu");
        CalibreBook[] books = [first, second];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "library"), group, books, [], [], [], CancellationToken.None);
        UserRecommendationOverride proposed = new(
            generated.ModelVersion,
            generated.InputVersion,
            RecommendationReviewStatus.ManuallyAdjusted,
            DateTimeOffset.UnixEpoch,
            retainedSeparateBookIds: []);

        RecommendationOverrideOutcome outcome = ApplyRecommendationOverrideUseCase.Execute(generated, proposed);

        outcome.IsSuccess.Should().BeTrue();
        outcome.Reviewed!.EffectiveSelection.Should().BeNull();
        outcome.Reviewed.Generated.RetainedSeparateRecords.Select(value => value.BookId).Should().BeEquivalentTo(generated.MemberIds);
    }

    [Fact]
    public async Task ExportBuildsSanitizedDocumentAndCallsOnlyExternalBoundary()
    {
        LibrarySnapshot baseSnapshot = Snapshot();
        ConsolidationRecommendation[] generated = (await new GenerateConsolidationRecommendationsUseCase(new()).ExecuteAsync(baseSnapshot, null, CancellationToken.None)).ToArray();
        LibrarySnapshot snapshot = new(baseSnapshot.Identity, baseSnapshot.ScannedAt, baseSnapshot.Books, baseSnapshot.Findings, baseSnapshot.ExactBinaryDuplicateGroups, baseSnapshot.ExactMetadataDuplicateGroups, baseSnapshot.EpubAssessments, generated);
        ReviewedConsolidationRecommendation[] reviewed = generated.Select(ApplyRecommendationOverrideUseCase.Reset).ToArray();
        IRecommendationExporter exporter = A.Fake<IRecommendationExporter>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(DateTimeOffset.UnixEpoch);
        A.CallTo(() => exporter.ExportAsync(A<RecommendationReviewExportDocument>._, "library", "outside/review.json", A<CancellationToken>._))
            .Returns(RecommendationExportWriteOutcome.Success("outside/review.json"));

        RecommendationExportWriteOutcome outcome = await new ExportRecommendationsUseCase(exporter, clock).ExecuteAsync(snapshot, reviewed, "outside/review.json", CancellationToken.None);

        outcome.IsSuccess.Should().BeTrue();
        A.CallTo(() => exporter.ExportAsync(
            A<RecommendationReviewExportDocument>.That.Matches(document => document.SourceLibraryUuid == snapshot.Identity.CalibreLibraryUuid && document.ExportedAtUtc == DateTimeOffset.UnixEpoch),
            "library",
            "outside/review.json",
            A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void UndefinedOverrideValuesReturnStructuredValidationErrors()
    {
        ConsolidationRecommendation generated = Generate();
        UserRecommendationOverride proposed = new(
            generated.ModelVersion,
            generated.InputVersion,
            (RecommendationReviewStatus)999,
            DateTimeOffset.UnixEpoch,
            formatOverrides: [new("PDF", (FormatOverrideAction)999)]);

        RecommendationOverrideOutcome outcome = ApplyRecommendationOverrideUseCase.Execute(generated, proposed);

        outcome.IsSuccess.Should().BeFalse();
        outcome.Errors.Should().Contain(value => value.Code == "REVIEW_STATUS_INVALID");
        outcome.Errors.Should().Contain(value => value.Code == "FORMAT_ACTION_INVALID");
    }

    [Fact]
    public async Task ExportRejectsReviewWhoseGeneratedValueIsNotTheSnapshotValue()
    {
        LibrarySnapshot baseSnapshot = Snapshot();
        ConsolidationRecommendation generated = (await new GenerateConsolidationRecommendationsUseCase(new()).ExecuteAsync(baseSnapshot, null, CancellationToken.None)).Single();
        LibrarySnapshot snapshot = new(baseSnapshot.Identity, baseSnapshot.ScannedAt, baseSnapshot.Books, baseSnapshot.Findings, baseSnapshot.ExactBinaryDuplicateGroups, baseSnapshot.ExactMetadataDuplicateGroups, baseSnapshot.EpubAssessments, [generated]);
        ConsolidationRecommendation copy = new(generated.GroupId, generated.MemberIds, generated.ModelVersion, generated.InputVersion, generated.MetadataSource, generated.FormatSelections, generated.RecordRecommendations, generated.Reasons, generated.Warnings, generated.Confidence);
        IRecommendationExporter exporter = A.Fake<IRecommendationExporter>();
        IClock clock = A.Fake<IClock>();

        RecommendationExportWriteOutcome outcome = await new ExportRecommendationsUseCase(exporter, clock).ExecuteAsync(
            snapshot,
            [ApplyRecommendationOverrideUseCase.Reset(copy)],
            "outside/review.json",
            CancellationToken.None);

        outcome.Error!.Code.Should().Be("REVIEW_STATE_MISMATCH");
        A.CallTo(() => exporter.ExportAsync(A<RecommendationReviewExportDocument>._, A<string>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    private static LibrarySnapshot Snapshot()
    {
        CalibreBook[] books = [Book(1), Book(2)];
        return new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "library"),
            DateTimeOffset.UnixEpoch,
            books,
            [],
            ExactBinaryDuplicateDetector.Detect(books),
            ExactMetadataDuplicateDetector.Detect(books));
    }

    private static ConsolidationRecommendation Generate()
    {
        LibrarySnapshot snapshot = Snapshot();
        return new ConsolidationRecommendationPolicy().Generate(snapshot.Identity, snapshot.ExactMetadataDuplicateGroups.Single(), snapshot.Books, snapshot.ExactBinaryDuplicateGroups, [], [], CancellationToken.None);
    }

    private static CalibreBook Book(long id)
    {
        FormatFileFingerprint fingerprint = new(id, new(new string((char)('a' + id), 64)));
        BookFormat[] formats = id == 1 ? [new BookFormat(
            "PDF",
            "book",
            $"book-{id}.pdf",
            FormatFileStatus.Present,
            fingerprint,
            new FormatFileObservation(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))] : [];
        return new(new(id), "Shared", "Author", [new(new(id), "Author", "Author")], [], formats, $"Book ({id})");
    }

    private static CalibreBook BookWithLanguage(long id, string language) => new(
        new(id), "Shared", "Author", [new(new(id), "Author", "Author")], [], [], $"Book ({id})", new(languages: [language]));

    private static CalibreBook ScaleBook(long id, string title) => new(
        new(id), title, "Author", [new(new(id), "Author", "Author")], [], [], $"Book ({id})");

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
