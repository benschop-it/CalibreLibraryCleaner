using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class QuarantineReviewSubjectRowViewModelTests
{
    [Fact]
    public void PendingRowsExposeRouteCoverageWithoutSelectingTransfer()
    {
        FinalRecordQualificationEvidence evidence = new(
            false, false, FinalRecordContentStatus.Incomplete, true, true, false, false,
            [new("local-PDF", "2.0", FinalRecordRouteOutcome.Unavailable)]);
        QuarantineReviewSubject subject = new(
            new(9), "The Book", [new(new(4), "Reliable Writer", "Writer, Reliable")], [],
            new(["TITLE.UNBALANCED_DELIMITER"], [],
                FinalRecordQualificationPolicy.Assess(evidence), evidence));
        QuarantineReviewSubjectRowViewModel row = new(subject, (_, _) => Task.CompletedTask);

        row.Include.Should().BeFalse();
        row.Coverage.Should().Be("local-PDF: Unavailable");
        row.HasDetectedProblem.Should().BeFalse("incomplete evidence is not a catalog defect");
        row.IsVisible(false).Should().BeFalse();
        row.IsVisible(true).Should().BeTrue();
        row.VerificationSummary.Should().Contain("did not confirm");
        row.ProblemSummary.Should().Contain("No specific");
    }

    [Fact]
    public async Task RowDisplaysProvenanceAndPersistsInclude()
    {
        List<(CalibreBookId Id, bool Include)> persisted = [];
        FinalRecordDispositionLabel label = new(
            FinalRecordDispositionLabelSource.ManagedLayout,
            "Unknown");
        QuarantineReviewSubject subject = new(
            new(7),
            "The Book",
            [new(new(3), "Reliable Author", "Author, Reliable")],
            [new("EPUB", "Author/Book (7)/book.epub", FormatFileStatus.Present, null)],
            new([FinalRecordDispositionReasonCodes.PlaceholderLabel], [label]));
        QuarantineReviewSubjectRowViewModel row = new(subject, (id, include) =>
        {
            persisted.Add((id, include));
            return Task.CompletedTask;
        });

        row.Provenance.Should().Be("ManagedLayout: Unknown");
        row.Reasons.Should().Contain(FinalRecordDispositionReasonCodes.PlaceholderLabel);
        row.Include = false;
        await row.PersistIncludeCommand.ExecuteAsync(null);

        persisted.Should().Equal((new CalibreBookId(7), false));
        row.IsVisible(false).Should().BeFalse();
        row.IsProposed.Should().BeTrue();
    }

    [Fact]
    public void ManualRowsRemainDistinctFromPolicyProposals()
    {
        QuarantineReviewSubject subject = new(
            new(8),
            "Ordinary Book",
            [new(new(4), "Reliable Author", "Author, Reliable")],
            [],
            new([], []),
            manuallyIncluded: true);
        QuarantineReviewSubjectRowViewModel row = new(subject, (_, _) => Task.CompletedTask);

        row.IsManual.Should().BeTrue();
        row.IsProposed.Should().BeFalse();
        row.IsVisible(false).Should().BeTrue("checked moves remain visible even without a detected defect");
        row.TransferChoice.Should().Contain("You checked");
    }

    [Theory]
    [InlineData("Jane Writer - A Book", "Jane Writer", "title includes")]
    [InlineData("A Book", "A Book", "identical")]
    [InlineData("A Book", "Jane Writer [12]", "bracketed number")]
    public void ConcreteProblemsHaveReadableDescriptionsAndAnInspectionAction(string title, string author, string explanation)
    {
        QuarantineReviewSubject subject = new(new(1), title, [new(new(1), author, author)], [], new([], []));
        QuarantineReviewSubjectRowViewModel row = new(subject, (_, _) => Task.CompletedTask);

        row.HasDetectedProblem.Should().BeTrue();
        row.IsVisible(false).Should().BeTrue();
        row.ProblemSummary.Should().Contain(explanation).And.NotContain("AUTHOR.");
        row.SuggestedAction.Should().Contain("Double-click").And.Contain("Leave Move unchecked");
        row.Include.Should().BeFalse("new review-only signals never authorize an automatic transfer");
    }

    [Fact]
    public void ExplicitlyAcceptedLegitimateWarningLeavesTheProblemListButRemainsInspectable()
    {
        FinalRecordQualificationEvidence evidence = new(
            false, false, FinalRecordContentStatus.Usable, true, false, false, false, [],
            reviewerAccepted: true);
        QuarantineReviewSubject subject = new(new(1), "1997 The Journey",
            [new(new(1), "Jane Writer", "Writer, Jane")], [],
            new([], [], FinalRecordQualification.Qualified, evidence));
        QuarantineReviewSubjectRowViewModel row = new(subject, (_, _) => Task.CompletedTask);

        row.HasMetadataProblem.Should().BeTrue();
        row.HasDetectedProblem.Should().BeFalse("the bound reviewer decision resolved the warning");
        row.IsVisible(false).Should().BeFalse();
        row.IsVisible(true).Should().BeTrue();
        row.ProblemSummary.Should().Contain("Accepted by you");
    }

    [Fact]
    public void ConfirmedUnusableContentIsAProblemWithoutInventingAnAuthorDefect()
    {
        FinalRecordQualificationEvidence evidence = new(
            true, true, FinalRecordContentStatus.ConfirmedUnusable, true, false, false, false, []);
        QuarantineReviewSubject subject = new(new(1), "A Book", [new(new(1), "Jane Writer", "Writer, Jane")], [],
            new([], [], FinalRecordQualification.ProposedUnresolvable, evidence));
        QuarantineReviewSubjectRowViewModel row = new(subject, (_, _) => Task.CompletedTask);

        row.HasMetadataProblem.Should().BeFalse();
        row.HasDetectedProblem.Should().BeTrue();
        row.ProblemSummary.Should().Be("No usable retained format was found.");
    }
}
