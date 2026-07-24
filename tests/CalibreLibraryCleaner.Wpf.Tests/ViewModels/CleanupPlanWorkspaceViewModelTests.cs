using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Plans;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FakeItEasy;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class CleanupPlanWorkspaceViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GenerateApproveAndRevokeShowCompleteImmutableRevisionsWithoutStoreAccess()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        ICleanupPlanStore store = A.Fake<ICleanupPlanStore>();
        ICleanupPlanConfirmationService confirmation = A.Fake<ICleanupPlanConfirmationService>();
        A.CallTo(() => confirmation.ConfirmApproval(A<CleanupPlan>._)).Returns(true);
        A.CallTo(() => confirmation.ConfirmRevocation(A<CleanupPlan>._, A<string>._)).Returns(true);
        CleanupPlanWorkspaceViewModel workspace = Create(store, confirmation);
        workspace.UpdateContext(snapshot, reviewed);

        workspace.GenerateCommand.Execute(null);

        workspace.Plans.Should().ContainSingle(value => value.State == "Valid");
        workspace.FormatRetentions.Should().ContainSingle(value => value.Format == "PDF");
        workspace.ExpectedFormats.Should().ContainSingle(value => value.Sha256 == new string('a', 64));
        workspace.BackupRequirements.Should().Contain(value => value.Status == "Required; not created");
        workspace.Issues.Should().Contain(value => value.Code == "PLAN.NON_EXECUTABLE");
        workspace.ApproveCommand.Execute(null);
        workspace.SelectedPlan!.State.Should().Be("Approved");
        workspace.RevocationReason = "No longer desired.";
        workspace.RevokeCommand.Execute(null);
        workspace.SelectedPlan!.State.Should().Be("Revoked");
        workspace.Plans.Should().HaveCount(3);
        workspace.LifecycleHistory.Should().HaveCount(3);
        A.CallTo(() => store.WriteAsync(A<CleanupPlan>._, A<string>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
        A.CallTo(() => store.ReadAsync(A<string>._, A<string>._, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public void DeferredRecommendationShowsBlockingIssueAndCreatesNoPlan()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation eligible) = Eligible();
        ReviewedConsolidationRecommendation deferred = ApplyRecommendationOverrideUseCase.Execute(eligible.Generated, new(
            eligible.Generated.ModelVersion, eligible.Generated.InputVersion, RecommendationReviewStatus.Deferred, Now)).Reviewed!;
        CleanupPlanWorkspaceViewModel workspace = Create(A.Fake<ICleanupPlanStore>(), A.Fake<ICleanupPlanConfirmationService>());
        workspace.UpdateContext(snapshot, deferred);

        workspace.GenerateCommand.Execute(null);

        workspace.Plans.Should().BeEmpty();
        workspace.GenerationIssues.Should().Contain(value => value.Code == "PLAN.REVIEW_DEFERRED" && value.Severity == "BlockingError");
        workspace.Status.Should().Contain("No cleanup plan");
    }

    [Fact]
    public void ChangingCurrentReviewImmediatelyStalesExistingApprovedPlan()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        ICleanupPlanConfirmationService confirmation = A.Fake<ICleanupPlanConfirmationService>();
        A.CallTo(() => confirmation.ConfirmApproval(A<CleanupPlan>._)).Returns(true);
        CleanupPlanWorkspaceViewModel workspace = Create(A.Fake<ICleanupPlanStore>(), confirmation);
        workspace.UpdateContext(snapshot, reviewed);
        workspace.GenerateCommand.Execute(null);
        workspace.ApproveCommand.Execute(null);
        ReviewedConsolidationRecommendation keepSeparate = ApplyRecommendationOverrideUseCase.Execute(reviewed.Generated, new(
            reviewed.Generated.ModelVersion, reviewed.Generated.InputVersion, RecommendationReviewStatus.KeepSeparate,
            Now.AddMinutes(1), retainedSeparateBookIds: reviewed.Generated.MemberIds)).Reviewed!;

        workspace.UpdateContext(snapshot, keepSeparate);

        workspace.SelectedPlan!.State.Should().Be("Stale");
        workspace.ApproveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void SwitchingPhysicalLibraryRootsClearsSessionPlansAndDisablesExport()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        CleanupPlanWorkspaceViewModel workspace = Create(A.Fake<ICleanupPlanStore>(), A.Fake<ICleanupPlanConfirmationService>());
        workspace.UpdateContext(snapshot, reviewed);
        workspace.GenerateCommand.Execute(null);
        LibraryIdentity movedIdentity = new(snapshot.Identity.CalibreLibraryUuid, snapshot.Identity.SchemaVersion, "D:\\other-library");
        LibrarySnapshot moved = new(movedIdentity, snapshot.ScannedAt, snapshot.Books, snapshot.Findings,
            snapshot.ExactBinaryDuplicateGroups, snapshot.ExactMetadataDuplicateGroups, snapshot.EpubAssessments,
            snapshot.ConsolidationRecommendations);

        workspace.UpdateContext(moved, reviewed);

        workspace.Plans.Should().BeEmpty();
        workspace.ExportCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ImportedApprovalRemainsPersistentlyLabeledInformational()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        ICleanupPlanStore store = A.Fake<ICleanupPlanStore>();
        ICleanupPlanFilePicker picker = A.Fake<ICleanupPlanFilePicker>();
        CleanupPlan valid = GenerateForTest(snapshot, reviewed);
        CleanupPlan approved = CleanupPlanLifecyclePolicy.Approve(valid, valid.Validation, Now.AddMinutes(1));
        A.CallTo(() => picker.PickImportSource()).Returns("outside.cleanup-plan.json");
        A.CallTo(() => store.ReadAsync(snapshot.Identity.LibraryRoot, "outside.cleanup-plan.json", A<CancellationToken>._))
            .Returns(CleanupPlanStoreReadResult.Success(approved));
        CleanupPlanWorkspaceViewModel workspace = Create(store, A.Fake<ICleanupPlanConfirmationService>(), picker);
        workspace.UpdateContext(snapshot, reviewed);

        workspace.ImportCommand.Execute(null);
        await workspace.ImportCommand.ExecutionTask!;

        workspace.SelectedPlan!.IsImported.Should().BeTrue();
        workspace.SelectedPlan.ApprovalState.Should().Be("Imported approval (informational)");
        workspace.ApprovalSummary.Should().StartWith("Imported approval (informational only).");
    }

    [Fact]
    public void SamePlanIdCannotBeReusedForChangedReviewBody()
    {
        (LibrarySnapshot snapshot, ReviewedConsolidationRecommendation reviewed) = Eligible();
        CleanupPlanWorkspaceViewModel workspace = Create(A.Fake<ICleanupPlanStore>(), A.Fake<ICleanupPlanConfirmationService>());
        workspace.UpdateContext(snapshot, reviewed);
        workspace.GenerateCommand.Execute(null);
        ReviewedConsolidationRecommendation adjusted = ApplyRecommendationOverrideUseCase.Execute(reviewed.Generated, new(
            reviewed.Generated.ModelVersion, reviewed.Generated.InputVersion, RecommendationReviewStatus.ManuallyAdjusted,
            Now.AddMinutes(1), metadataSourceBookId: new CalibreBookId(2))).Reviewed!;

        workspace.UpdateContext(snapshot, adjusted);
        workspace.GenerateCommand.Execute(null);

        workspace.Plans.Should().NotContain(value => value.TargetRecordId == 2 && value.State == "Valid");
        workspace.Status.Should().Contain("plan ID belongs to a different immutable body");
    }

    private static CleanupPlanWorkspaceViewModel Create(
        ICleanupPlanStore store,
        ICleanupPlanConfirmationService confirmation,
        ICleanupPlanFilePicker? picker = null)
    {
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        picker ??= A.Fake<ICleanupPlanFilePicker>();
        A.CallTo(() => ids.Create()).Returns(new CleanupPlanId(Guid.Parse("11111111-2222-3333-4444-555555555555")));
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        ValidateCleanupPlanUseCase validator = new(clock);
        return new(
            new(ids, clock),
            validator,
            new(clock),
            new(clock),
            new(store),
            new(store, validator),
            picker,
            confirmation);
    }

    private static CleanupPlan GenerateForTest(
        LibrarySnapshot snapshot,
        ReviewedConsolidationRecommendation reviewed)
    {
        ICleanupPlanIdGenerator ids = A.Fake<ICleanupPlanIdGenerator>();
        IClock clock = A.Fake<IClock>();
        A.CallTo(() => ids.Create()).Returns(new CleanupPlanId(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")));
        A.CallTo(() => clock.GetUtcNow()).Returns(Now);
        return new GenerateCleanupPlanUseCase(ids, clock).Execute(snapshot, reviewed).Plan!;
    }

    private static (LibrarySnapshot Snapshot, ReviewedConsolidationRecommendation Reviewed) Eligible()
    {
        FormatFileFingerprint fingerprint = new(5, new(new string('a', 64)));
        CalibreBook[] books =
        [
            new(new(1), "Shared", "Author", [new(new(1), "Author", "Author")], [],
                [new("PDF", "book", "Author/Shared (1)/book.pdf", FormatFileStatus.Present, fingerprint,
                    new FormatFileObservation(5, Now.AddDays(-1), Now.AddHours(-1), 32))],
                "Author/Shared (1)", new(languages: ["eng"])),
            new(new(2), "Shared", "Author", [new(new(2), "Author", "Author")], [], [],
                "Author/Shared (2)", new(languages: ["eng"])),
        ];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        LibraryIdentity identity = new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\synthetic\\library");
        ConsolidationRecommendation generated = new ConsolidationRecommendationPolicy().Generate(
            identity, group, books, [], [], [], CancellationToken.None);
        ReviewedConsolidationRecommendation reviewed = ApplyRecommendationOverrideUseCase.Execute(generated, new(
            generated.ModelVersion, generated.InputVersion, RecommendationReviewStatus.Accepted, Now)).Reviewed!;
        return (new(identity, Now, books, [], [], [group], [], [generated]), reviewed);
    }
}
