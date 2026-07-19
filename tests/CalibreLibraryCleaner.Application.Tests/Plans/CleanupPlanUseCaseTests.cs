using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Plans;

public sealed class CleanupPlanUseCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CurrentAcceptedReviewGeneratesCompleteImmutableValidPlan()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => ids.Create()).Returns(new CleanupPlanId(Guid.Parse("11111111-2222-3333-4444-555555555555")));
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);

        CleanupPlanGenerationOutcome outcome = new GenerateCleanupPlanUseCase(ids, clock).Execute(snapshot, reviewed);

        outcome.IsSuccess.Should().BeTrue();
        CleanupPlan plan = outcome.Plan!;
        plan.State.Should().Be(CleanupPlanState.Valid);
        plan.Definition.TargetRecordId.Should().Be(plan.Definition.MetadataRetention.SourceRecordId);
        plan.Definition.RecordRemovals.Should().ContainSingle().Which.RecordId.Should().Be(new CalibreBookId(2));
        plan.Definition.FormatRetentions.Should().ContainSingle(value => value.Format == "PDF");
        plan.Definition.BackupRequirements.Should().Contain(value => value.Kind == BackupRequirementKind.FormatFile);
        plan.Definition.BackupRequirements.Should().Contain(value => value.Kind == BackupRequirementKind.CleanupPlanArtifact);
        plan.Definition.BackupRequirements.Should().Contain(value => value.Kind == BackupRequirementKind.ExecutionAudit);
        plan.Validation.BlockingErrors.Should().BeEmpty();
        plan.Validation.Information.Should().Contain(value => value.Code == "PLAN.NON_EXECUTABLE");
        plan.ContentDigest.Should().Be(CleanupPlanContentDigestPolicy.Compute(plan.Definition));
    }

    [Theory]
    [InlineData(RecommendationReviewStatus.Unreviewed, "PLAN.REVIEW_REQUIRED")]
    [InlineData(RecommendationReviewStatus.Deferred, "PLAN.REVIEW_DEFERRED")]
    [InlineData(RecommendationReviewStatus.KeepSeparate, "PLAN.RECORDS_KEEP_SEPARATE")]
    [InlineData(RecommendationReviewStatus.NotDuplicates, "PLAN.NOT_DUPLICATES")]
    public void IneligibleReviewStatusReturnsBlockingIssueWithoutAllocatingIdentity(
        RecommendationReviewStatus status,
        string code)
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation eligible) = Eligible();
        ReviewedConsolidationRecommendation reviewed = status == RecommendationReviewStatus.Unreviewed
            ? ApplyRecommendationOverrideUseCase.Reset(eligible.Generated)
            : ApplyRecommendationOverrideUseCase.Execute(eligible.Generated, new(
                eligible.Generated.ModelVersion,
                eligible.Generated.InputVersion,
                status,
                Now,
                retainedSeparateBookIds: status == RecommendationReviewStatus.KeepSeparate ? eligible.Generated.MemberIds : [])).Reviewed!;
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);

        CleanupPlanGenerationOutcome outcome = new GenerateCleanupPlanUseCase(ids, clock).Execute(snapshot, reviewed);

        outcome.Plan.Should().BeNull();
        outcome.Validation.BlockingErrors.Should().Contain(value => value.Code == code);
        A.CallTo(() => ids.Create()).MustNotHaveHappened();
    }

    [Fact]
    public void ApprovalAndRevocationCreateImmutableRevisionsBoundToBodyDigest()
    {
        CleanupPlan valid = Generate();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).ReturnsNextFromSequence(Now.AddMinutes(1), Now.AddMinutes(2));

        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        CleanupPlan approved = new ApproveCleanupPlanUseCase(clock).Execute(valid, snapshot, reviewed).Plan!;
        CleanupPlan revoked = new RevokeCleanupPlanUseCase(clock).Execute(approved, "User withdrew consent.").Plan!;

        approved.Should().NotBeSameAs(valid);
        approved.Definition.Should().BeSameAs(valid.Definition);
        approved.ContentDigest.Should().Be(valid.ContentDigest);
        approved.State.Should().Be(CleanupPlanState.Approved);
        approved.Approval!.ContentDigest.Should().Be(valid.ContentDigest);
        revoked.State.Should().Be(CleanupPlanState.Revoked);
        revoked.Definition.Should().BeSameAs(valid.Definition);
        revoked.Revocation!.ContentDigest.Should().Be(valid.ContentDigest);
        revoked.LifecycleHistory.Should().HaveCount(3);
        new ApproveCleanupPlanUseCase(clock).Execute(revoked, snapshot, reviewed).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void ChangedObservedFileStateMakesApprovedPlanStaleAndPreventsApproval()
    {
        CleanupPlan approved = Approve(Generate());
        (LibrarySnapshot original, _) = Eligible();
        CalibreBook first = original.Books.Single(value => value.Id == new CalibreBookId(1));
        BookFormat old = first.Formats.Single();
        BookFormat changed = new(old.Format, old.StoredFileName, old.ExpectedRelativePath, old.FileStatus, old.Fingerprint,
            new(old.Observation!.Length, old.Observation.CreationTimeUtc, old.Observation.LastWriteTimeUtc.AddSeconds(1), old.Observation.Attributes));
        CalibreBook[] books =
        [
            new(first.Id, first.Title, first.AuthorSort, first.Authors, first.Identifiers, [changed], first.RelativeDirectory, first.PublicationMetadata),
            original.Books.Single(value => value.Id == new CalibreBookId(2)),
        ];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        ConsolidationRecommendation recommendation = new ConsolidationRecommendationPolicy().Generate(
            original.Identity, group, books, [], [], [], CancellationToken.None);
        LibrarySnapshot current = new(original.Identity, original.ScannedAt.AddMinutes(1), books, [], [], [group], [], [recommendation]);
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddHours(1));

        CleanupPlan stale = new ValidateCleanupPlanUseCase(clock).Execute(approved, current).Plan!;

        stale.State.Should().Be(CleanupPlanState.Stale);
        stale.Approval.Should().Be(approved.Approval);
        stale.Validation.BlockingErrors.Should().Contain(value => value.Code == "PLAN.STALE");
        (LibrarySnapshot eligibleSnapshot, ReviewedConsolidationRecommendation eligibleReviewed) = Eligible();
        new ApproveCleanupPlanUseCase(clock).Execute(stale, eligibleSnapshot, eligibleReviewed).IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void RegenerationAlwaysUsesNewPlanIdentity()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => ids.Create()).ReturnsNextFromSequence(
            new CleanupPlanId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new CleanupPlanId(Guid.Parse("22222222-2222-2222-2222-222222222222")));
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        GenerateCleanupPlanUseCase useCase = new(ids, clock);

        CleanupPlan first = useCase.Execute(snapshot, reviewed).Plan!;
        CleanupPlan second = useCase.Execute(snapshot, reviewed).Plan!;

        first.Id.Should().NotBe(second.Id);
        first.ContentDigest.Should().Be(second.ContentDigest);
    }

    [Fact]
    public void StaleUnresolvedExcludedAndOutsideGroupSelectionsAreRejectedDeterministically()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation eligible) = Eligible();
        ReviewedConsolidationRecommendation stale = eligible with { Freshness = RecommendationFreshness.Stale };
        ReviewedConsolidationRecommendation unresolved = ApplyRecommendationOverrideUseCase.Execute(eligible.Generated, new(
            eligible.Generated.ModelVersion, eligible.Generated.InputVersion, RecommendationReviewStatus.ManuallyAdjusted, Now,
            formatOverrides: [new("PDF", FormatOverrideAction.MarkUnresolved)])).Reviewed!;
        ReviewedConsolidationRecommendation excluded = ApplyRecommendationOverrideUseCase.Execute(eligible.Generated, new(
            eligible.Generated.ModelVersion, eligible.Generated.InputVersion, RecommendationReviewStatus.ManuallyAdjusted, Now,
            formatOverrides: [new("PDF", FormatOverrideAction.ExcludeFinalFormat)])).Reviewed!;
        ReviewedConsolidationRecommendation outside = eligible with
        {
            EffectiveSelection = eligible.EffectiveSelection! with { MetadataSourceBookId = new CalibreBookId(999) },
        };
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        GenerateCleanupPlanUseCase useCase = new(ids, clock);

        useCase.Execute(snapshot, stale).Validation.BlockingErrors.Should().Contain(value => value.Code == "PLAN.RECOMMENDATION_STALE");
        useCase.Execute(snapshot, unresolved).Validation.BlockingErrors.Should().Contain(value => value.Code == "PLAN.FORMAT_UNRESOLVED");
        useCase.Execute(snapshot, excluded).Validation.BlockingErrors.Should().Contain(value => value.Code == "PLAN.FORMAT_EXCLUDED");
        useCase.Execute(snapshot, outside).Validation.BlockingErrors.Should().Contain(value => value.Code == "PLAN.TARGET_INVALID");
        A.CallTo(() => ids.Create()).MustNotHaveHappened();
    }

    [Fact]
    public void LargeGroupFormatSetIsGeneratedWithCompleteDeterministicCoverage()
    {
        const int count = 1_000;
        BookFormat[] formats = Enumerable.Range(0, count).Select(index =>
        {
            string format = $"F{index:D4}";
            FormatFileFingerprint fingerprint = new(index + 1, new(new string((char)('a' + index % 6), 64)));
            return new BookFormat(format, $"book-{index}", $"Author/Shared (1)/book-{index}.{format.ToLowerInvariant()}",
                FormatFileStatus.Present, fingerprint, new(index + 1, Now, Now, 0));
        }).ToArray();
        CalibreBook[] books =
        [
            new(new(1), "Shared", "Author", [new(new(1), "Author", "Author")], [], formats, "Author/Shared (1)"),
            new(new(2), "Shared", "Author", [new(new(2), "Author", "Author")], [], [], "Author/Shared (2)"),
        ];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        LibraryIdentity identity = new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\synthetic\\library");
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(identity, group, books, [], [], [], CancellationToken.None);
        ReviewedConsolidationRecommendation reviewed = ApplyRecommendationOverrideUseCase.Execute(generated, new(
            generated.ModelVersion, generated.InputVersion, RecommendationReviewStatus.Accepted, Now)).Reviewed!;
        LibrarySnapshot snapshot = new(identity, Now, books, [], [], [group], [], [generated]);
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => ids.Create()).Returns(new CleanupPlanId(Guid.Parse("11111111-2222-3333-4444-555555555555")));
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);

        CleanupPlan plan = new GenerateCleanupPlanUseCase(ids, clock).Execute(snapshot, reviewed).Plan!;

        plan.Definition.FormatRetentions.Should().HaveCount(count);
        plan.Definition.ExpectedLibraryState.Records.SelectMany(value => value.Formats).Should().HaveCount(count);
        plan.Definition.BackupRequirements.Count(value => value.Kind == BackupRequirementKind.FormatFile).Should().Be(count);
        plan.Definition.FormatRetentions.Select(value => value.Format).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void LegalTransitionMatrixIsExactAndApprovedBodyHasNoPublicMutationSurface()
    {
        HashSet<(CleanupPlanState From, CleanupPlanState To)> expected =
        [
            (CleanupPlanState.Draft, CleanupPlanState.Valid),
            (CleanupPlanState.Draft, CleanupPlanState.Blocked),
            (CleanupPlanState.Valid, CleanupPlanState.Approved),
            (CleanupPlanState.Valid, CleanupPlanState.Stale),
            (CleanupPlanState.Valid, CleanupPlanState.Blocked),
            (CleanupPlanState.Approved, CleanupPlanState.Stale),
            (CleanupPlanState.Approved, CleanupPlanState.Revoked),
        ];

        foreach (CleanupPlanState from in Enum.GetValues<CleanupPlanState>())
            foreach (CleanupPlanState to in Enum.GetValues<CleanupPlanState>())
                CleanupPlan.IsLegal(from, to).Should().Be(expected.Contains((from, to)));

        CleanupPlan approved = Approve(Generate());
        typeof(CleanupPlan).GetProperties().Should().OnlyContain(property => property.SetMethod == null);
        typeof(CleanupPlanDefinition).GetProperties().Should().OnlyContain(property => property.SetMethod == null);
        Action mutate = () => ((IList<FormatRetentionInstruction>)approved.Definition.FormatRetentions).Clear();
        mutate.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void InGroupEffectiveSelectionThatWasNotProducedByOverrideIsRejected()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        ReviewedConsolidationRecommendation forged = reviewed with
        {
            EffectiveSelection = reviewed.EffectiveSelection! with { MetadataSourceBookId = new CalibreBookId(2) },
        };
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);

        CleanupPlanGenerationOutcome outcome = new GenerateCleanupPlanUseCase(ids, clock).Execute(snapshot, forged);

        outcome.Plan.Should().BeNull();
        outcome.Validation.BlockingErrors.Should().Contain(value => value.Code == "PLAN.REVIEW_INCONSISTENT");
        A.CallTo(() => ids.Create()).MustNotHaveHappened();
    }

    [Fact]
    public void ChangedCurrentReviewMakesExistingApprovedPlanStale()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        CleanupPlan approved = Approve(Generate());
        ReviewedConsolidationRecommendation keepSeparate = ApplyRecommendationOverrideUseCase.Execute(reviewed.Generated, new(
            reviewed.Generated.ModelVersion, reviewed.Generated.InputVersion, RecommendationReviewStatus.KeepSeparate,
            Now.AddMinutes(2), retainedSeparateBookIds: reviewed.Generated.MemberIds)).Reviewed!;
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddMinutes(3));

        CleanupPlanOperationOutcome outcome = new ValidateCleanupPlanUseCase(clock).Execute(approved, snapshot, keepSeparate);

        outcome.Plan!.State.Should().Be(CleanupPlanState.Stale);
        outcome.Issues.Should().Contain(value => value.Code == "PLAN.STALE"
            && value.SubjectKind == CleanupPlanIssueSubjectKind.Provenance);
    }

    [Fact]
    public void ApprovalRequiresMatchingCurrentSnapshotAndReview()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        CleanupPlan valid = Generate();
        ReviewedConsolidationRecommendation keepSeparate = ApplyRecommendationOverrideUseCase.Execute(reviewed.Generated, new(
            reviewed.Generated.ModelVersion, reviewed.Generated.InputVersion, RecommendationReviewStatus.KeepSeparate,
            Now.AddMinutes(1), retainedSeparateBookIds: reviewed.Generated.MemberIds)).Reviewed!;
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddMinutes(2));

        CleanupPlanOperationOutcome outcome = new ApproveCleanupPlanUseCase(clock).Execute(valid, snapshot, keepSeparate);

        outcome.Plan.Should().BeNull();
        outcome.Issues.Should().ContainSingle(value => value.Code == "PLAN.APPROVAL_NOT_ALLOWED");
    }

    [Fact]
    public void SelectedSourceOnRemovedRecordHasExplicitPostRetentionRemoval()
    {
        FormatFileFingerprint fingerprint = new(5, new(new string('b', 64)));
        FormatFileObservation observation = new(5, Now.AddDays(-1), Now.AddHours(-1), 32);
        CalibreBook[] books =
        [
            new(new(1), "Shared", "Author", [new(new(1), "Author", "Author")], [], [], "Author/Shared (1)"),
            new(new(2), "Shared", "Author", [new(new(2), "Author", "Author")], [],
                [new("EPUB", "book", "Author/Shared (2)/book.epub", FormatFileStatus.Present, fingerprint, observation)],
                "Author/Shared (2)"),
        ];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        LibraryIdentity identity = new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\synthetic\\library");
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(identity, group, books, [], [], [], CancellationToken.None);
        ReviewedConsolidationRecommendation reviewed = ApplyRecommendationOverrideUseCase.Execute(generated, new(
            generated.ModelVersion, generated.InputVersion, RecommendationReviewStatus.Accepted, Now)).Reviewed!;
        LibrarySnapshot snapshot = new(identity, Now, books, [], [], [group], [], [generated]);
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => ids.Create()).Returns(new CleanupPlanId(Guid.Parse("33333333-2222-3333-4444-555555555555")));
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);

        CleanupPlan plan = new GenerateCleanupPlanUseCase(ids, clock).Execute(snapshot, reviewed).Plan!;

        plan.Definition.FormatRemovals.Should().ContainSingle(value => value.RecordId == new CalibreBookId(2)
            && value.Reason == FormatRemovalReason.RemovedWithSourceRecordAfterRetention
            && value.RetainedFormatInstructionId == "retain:EPUB");
        CleanupPlanSafetyPolicy.Validate(plan.Definition).Should().BeEmpty();
    }

    [Fact]
    public void SuccessfulValidationReturnsCurrentLocalValidationTimeWithoutMutatingPlanRevision()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        CleanupPlan valid = Generate();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddHours(2));

        CleanupPlanOperationOutcome outcome = new ValidateCleanupPlanUseCase(clock).Execute(valid, snapshot, reviewed);

        outcome.Plan.Should().BeSameAs(valid);
        outcome.Validation!.ValidatedAtUtc.Should().Be(Now.AddHours(2));
        outcome.Plan.ArtifactRevision.Should().Be(valid.ArtifactRevision);
    }

    [Fact]
    public void CanonicalDigestDistinguishesNullMetadataFromEmptyMetadata()
    {
        CleanupPlan plan = Generate();
        ExpectedLibraryState original = plan.Definition.ExpectedLibraryState;
        ExpectedRecordState first = original.Records[0];
        ExpectedRecordState changedRecord = new(first.RecordId, first.Title, first.AuthorSort, first.Authors, first.Identifiers,
            string.Empty, first.PublicationDate, first.Series, first.SeriesIndex, first.Languages, first.HasCover,
            first.RelativeDirectory, first.Formats);
        ExpectedLibraryState changedExpected = new(original.LibraryUuid, original.SchemaVersion, original.GroupId,
            original.MemberIds, [changedRecord, original.Records[1]], original.RecommendationModelVersion,
            original.RecommendationInputVersion);
        CleanupPlanDefinition changed = new(changedExpected, plan.Definition.TargetRecordId, plan.Definition.InvolvedRecordIds,
            plan.Definition.MetadataRetention, plan.Definition.FormatRetentions, plan.Definition.FormatRemovals,
            plan.Definition.RecordRemovals, plan.Definition.BackupRequirements, plan.Definition.Provenance);

        CleanupPlanContentDigestPolicy.Compute(changed).Should().NotBe(plan.ContentDigest);
    }

    [Fact]
    public void SamePlanIdWithDifferentFrozenBodyIsNotACompatibleLineage()
    {
        CleanupPlan first = Generate();
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => ids.Create()).Returns(first.Id);
        A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddMinutes(5));
        CleanupPlan second = new GenerateCleanupPlanUseCase(ids, clock).Execute(snapshot, reviewed).Plan!;

        CleanupPlanLineagePolicy.IsCompatible(first, second).Should().BeFalse();
    }

    internal static (LibrarySnapshot Snapshot, ReviewedConsolidationRecommendation Reviewed) Eligible()
    {
        FormatFileFingerprint fingerprint = new(5, new(new string('a', 64)));
        FormatFileObservation observation = new(5, Now.AddDays(-1), Now.AddHours(-1), 32);
        CalibreBook[] books =
        [
            new(new(1), "Shared", "Author", [new(new(1), "Author", "Author")], [new("isbn", "9780306406157")],
                [new("PDF", "book", "Author/Shared (1)/book.pdf", FormatFileStatus.Present, fingerprint, observation)],
                "Author/Shared (1)", new(languages: ["eng"], hasCover: true)),
            new(new(2), "Shared", "Author", [new(new(2), "Author", "Author")], [new("isbn", "9780306406157")], [],
                "Author/Shared (2)", new(languages: ["eng"])),
        ];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        LibraryIdentity identity = new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\synthetic\\library");
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            identity, group, books, [], [], [], CancellationToken.None);
        UserRecommendationOverride accepted = new(
            generated.ModelVersion, generated.InputVersion, RecommendationReviewStatus.Accepted, Now);
        ReviewedConsolidationRecommendation reviewed = ApplyRecommendationOverrideUseCase.Execute(generated, accepted).Reviewed!;
        return (new(identity, Now, books, [], [], [group], [], [generated]), reviewed);
    }

    internal static CleanupPlan Generate()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => ids.Create()).Returns(new CleanupPlanId(Guid.Parse("11111111-2222-3333-4444-555555555555")));
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        return new GenerateCleanupPlanUseCase(ids, clock).Execute(snapshot, reviewed).Plan!;
    }

    private static CleanupPlan Approve(CleanupPlan plan)
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => clock.GetUtcNow()).Returns(Now.AddMinutes(1));
        return new ApproveCleanupPlanUseCase(clock).Execute(plan, snapshot, reviewed).Plan!;
    }
}
