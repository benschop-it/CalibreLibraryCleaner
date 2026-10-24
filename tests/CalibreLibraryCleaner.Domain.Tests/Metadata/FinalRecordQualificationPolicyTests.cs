using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Metadata;

public sealed class FinalRecordQualificationPolicyTests
{
    [Theory]
    [InlineData(FinalRecordRouteOutcome.NotAttempted)]
    [InlineData(FinalRecordRouteOutcome.Unavailable)]
    [InlineData(FinalRecordRouteOutcome.DeferredByBounds)]
    [InlineData(FinalRecordRouteOutcome.Conflicting)]
    public void IncompleteRoutesCannotAuthorizeTransfer(FinalRecordRouteOutcome outcome)
    {
        FinalRecordQualificationEvidence evidence = Evidence(
            [new("local", "1", FinalRecordRouteOutcome.CompletedWithoutEvidence),
             new("provider", "1", outcome)]);

        evidence.ResolutionExhausted.Should().BeFalse();
        FinalRecordQualificationPolicy.Assess(evidence).Should().Be(FinalRecordQualification.NeedsReview);
    }

    [Fact]
    public void EmptyOrNotNeededRoutesCannotInventExhaustion()
    {
        Evidence([]).ResolutionExhausted.Should().BeFalse();
        Evidence([new("local", "1", FinalRecordRouteOutcome.NotNeeded)])
            .ResolutionExhausted.Should().BeFalse();
    }

    [Fact]
    public void OnlyCompletedApplicableRoutesCanExhaustIdentityRepair()
    {
        FinalRecordQualificationEvidence evidence = Evidence(
            [new("local", "1", FinalRecordRouteOutcome.CompletedWithoutEvidence),
             new("provider", "1", FinalRecordRouteOutcome.CompletedWithoutEvidence)]);

        evidence.ResolutionExhausted.Should().BeTrue();
        FinalRecordQualificationPolicy.Assess(evidence)
            .Should().Be(FinalRecordQualification.ProposedUnresolvable);
    }

    [Theory]
    [InlineData(false, true, FinalRecordContentStatus.Usable, true)]
    [InlineData(true, false, FinalRecordContentStatus.Usable, true)]
    [InlineData(true, true, FinalRecordContentStatus.Incomplete, true)]
    [InlineData(true, true, FinalRecordContentStatus.Usable, false)]
    public void QualificationRequiresIndependentIdentityContentAndLayout(
        bool title, bool authors, FinalRecordContentStatus content, bool layout)
    {
        FinalRecordQualificationPolicy.Assess(new(
            title, authors, content, layout, false, false, false, []))
            .Should().Be(FinalRecordQualification.NeedsReview);
    }

    [Fact]
    public void CompleteEvidenceQualifiesWithoutOnlineLookup()
    {
        FinalRecordQualificationPolicy.Assess(new(
            true, true, FinalRecordContentStatus.Usable, true, false, false, false, []))
            .Should().Be(FinalRecordQualification.Qualified);
    }

    [Fact]
    public void RepairPrecedesTransferAndAcceptanceCannotSkipLayoutVerification()
    {
        FinalRecordQualificationPolicy.Assess(new(
            false, false, FinalRecordContentStatus.Usable, true, true, true, false, []))
            .Should().Be(FinalRecordQualification.Repairable);
        FinalRecordQualificationPolicy.Assess(new(
            false, false, FinalRecordContentStatus.Incomplete, false, true, false, false, [],
            reviewerAccepted: true))
            .Should().Be(FinalRecordQualification.NeedsReview);
    }

    private static FinalRecordQualificationEvidence Evidence(
        IEnumerable<FinalRecordResolutionRoute> routes) => new(
        false, false, FinalRecordContentStatus.Usable, true, true, false, false, routes);
}
