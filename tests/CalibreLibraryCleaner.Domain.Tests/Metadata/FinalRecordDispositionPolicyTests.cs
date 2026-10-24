using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class FinalRecordDispositionPolicyTests
{
    [Theory]
    [InlineData(AuthorResolutionStatus.Unresolved, FinalRecordDispositionReasonCodes.AuthorUnresolved)]
    [InlineData(AuthorResolutionStatus.Conflicting, FinalRecordDispositionReasonCodes.AuthorConflicting)]
    public void AuthorStatusAloneRemainsPendingWithExplicitReasons(
        AuthorResolutionStatus status,
        string expectedReason)
    {
        FinalRecordDispositionAssessment assessment = FinalRecordDispositionPolicy.Assess(
            status,
            [AuthorSuspicionReasonCodes.MatchesTitle],
            inspectionExhausted: false,
            []);

        assessment.ProposedForQuarantine.Should().BeFalse();
        assessment.Qualification.Should().Be(FinalRecordQualification.NeedsReview);
        assessment.ReasonCodes.Should().Equal(expectedReason);
    }

    [Fact]
    public void SuspectRequiresExhaustedInspectionAndStrongRemainingReason()
    {
        FinalRecordDispositionPolicy.Assess(
                AuthorResolutionStatus.Suspect,
                [AuthorSuspicionReasonCodes.MatchesTitle],
                inspectionExhausted: true,
                [])
            .ReasonCodes.Should().Equal(FinalRecordDispositionReasonCodes.AuthorSuspectExhausted);

        FinalRecordDispositionPolicy.Assess(
                AuthorResolutionStatus.Suspect,
                [AuthorSuspicionReasonCodes.MatchesTitle],
                inspectionExhausted: false,
                [])
            .ProposedForQuarantine.Should().BeFalse();
        FinalRecordDispositionPolicy.Assess(
                AuthorResolutionStatus.Suspect,
                [AuthorSuspicionReasonCodes.DisplayTypography],
                inspectionExhausted: true,
                [])
            .ProposedForQuarantine.Should().BeFalse();
    }

    public static TheoryData<string, FinalRecordDispositionLabelSource> PlaceholderLabels => new()
    {
        { "Unknown", FinalRecordDispositionLabelSource.Title },
        { " unkown ", FinalRecordDispositionLabelSource.Author },
        { "USER", FinalRecordDispositionLabelSource.Identifier },
        { "User", FinalRecordDispositionLabelSource.ManagedLayout },
    };

    [Theory]
    [MemberData(nameof(PlaceholderLabels))]
    public void ExactPlaceholderLabelsRemainReviewableRegardlessOfSource(
        string value,
        FinalRecordDispositionLabelSource source)
    {
        FinalRecordDispositionLabel label = new(source, value);

        FinalRecordDispositionAssessment assessment = FinalRecordDispositionPolicy.Assess(
            AuthorResolutionStatus.Known,
            [],
            inspectionExhausted: true,
            [label]);

        assessment.ReasonCodes.Should().Equal(FinalRecordDispositionReasonCodes.PlaceholderLabel);
        assessment.SupportingLabels.Should().Equal(label);
    }

    [Theory]
    [InlineData("Studio 54 Press")]
    [InlineData("User Guide")]
    [InlineData("The Unknown Soldier")]
    [InlineData("Q. 10 Public")]
    public void OrdinaryKnownRecordsRemainUnproposed(string value)
    {
        FinalRecordDispositionAssessment assessment = FinalRecordDispositionPolicy.Assess(
            AuthorResolutionStatus.Known,
            [],
            inspectionExhausted: true,
            [new(FinalRecordDispositionLabelSource.Author, value)]);

        assessment.ProposedForQuarantine.Should().BeFalse();
        assessment.ReasonCodes.Should().BeEmpty();
        assessment.SupportingLabels.Should().BeEmpty();
    }
}
