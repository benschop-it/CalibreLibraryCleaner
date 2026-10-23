using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Metadata;

public sealed class PrepareQuarantineReviewUseCaseTests
{
    private static readonly LibraryStateGenerationId Generation = new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    [Fact]
    public void ProposalsBindToCurrentStateAndFixedSiblingDestination()
    {
        LibraryState state = State([
            Book(1, "Ordinary Book", "Reliable Writer"),
            Book(2, "Unknown", "Reliable Writer"),
            Book(3, "Another Book", "Unknown"),
        ]);
        FinalAuthorReviewWorkspace finalAuthors = FinalAuthors(state);

        QuarantineReviewWorkspace workspace = PrepareQuarantineReviewUseCase.Execute(state, finalAuthors);

        workspace.LibraryRoot.Should().Be("C:\\Libraries\\Source");
        workspace.DestinationRoot.Should().Be("C:\\Libraries\\Source - Unresolvable");
        workspace.GenerationId.Should().Be(state.GenerationId);
        workspace.Revision.Should().Be(state.Revision);
        workspace.Subjects.Select(value => value.BookId.Value).Should().Equal(2, 3);
        workspace.Subjects.Should().OnlyContain(value => value.Include);
    }

    [Fact]
    public void AllFinalConflictingRowsDefaultIncluded()
    {
        LibraryState state = State([Book(1, "The Book", "Questionable Writer")]);
        FinalAuthorReviewSubject subject = FinalSubject(
            state.Snapshot.Books.Single(),
            new(
                AuthorResolutionStatus.Conflicting,
                CurrentAuthorProtection.Unverified,
                ["AUTHOR.CONFLICT"],
                []));
        FinalAuthorReviewWorkspace finalAuthors = FinalAuthors(state, [subject]);

        QuarantineReviewSubject quarantine = PrepareQuarantineReviewUseCase.Execute(state, finalAuthors)
            .Subjects.Should().ContainSingle().Subject;

        quarantine.Include.Should().BeTrue();
        quarantine.Disposition.ReasonCodes.Should().Contain(
            FinalRecordDispositionReasonCodes.AuthorConflicting);
    }

    [Fact]
    public void ManualInclusionMaterializesOnlyRequestedOrdinaryRecord()
    {
        LibraryState state = State([Book(1, "Ordinary Book", "Reliable Writer")]);
        QuarantineReviewWorkspace workspace = PrepareQuarantineReviewUseCase.Execute(
            state,
            FinalAuthors(state));

        QuarantineReviewWorkspace updated = PrepareQuarantineReviewUseCase.IncludeManually(
            state,
            workspace,
            new(1));

        QuarantineReviewSubject subject = updated.Subjects.Should().ContainSingle().Subject;
        subject.ManuallyIncluded.Should().BeTrue();
        subject.Include.Should().BeTrue();
        subject.Disposition.ProposedForQuarantine.Should().BeFalse();
    }

    [Fact]
    public void CheckedFinalAuthorCorrectionBlocksQuarantinePreparation()
    {
        LibraryState state = State([Book(1, "The Book", "Questionable Writer")]);
        FinalAuthorReviewSubject subject = FinalSubject(
            state.Snapshot.Books.Single(),
            new(
                AuthorResolutionStatus.Suspect,
                CurrentAuthorProtection.Unverified,
                [AuthorSuspicionReasonCodes.ConservativeNameOrder],
                [],
                AuthorProposalSource.ConservativeNameOrder,
                ["Reliable Writer"],
                ["Writer, Reliable"],
                selectedByDefault: true));
        FinalAuthorReviewWorkspace finalAuthors = FinalAuthors(state, [subject]);
        finalAuthors.Subjects.Should().ContainSingle().Which.Apply.Should().BeTrue();

        Action act = () => PrepareQuarantineReviewUseCase.Execute(state, finalAuthors);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no checked corrections*");
    }

    [Fact]
    public void StaleFinalAuthorWorkspaceIsRejected()
    {
        LibraryState state = State([Book(1, "Unknown", "Reliable Writer")]);
        FinalAuthorReviewWorkspace finalAuthors = FinalAuthors(state);
        LibraryState changed = new(
            state.GenerationId,
            new(1),
            LibraryStateStatus.Authoritative,
            state.Snapshot,
            DateTimeOffset.UnixEpoch,
            workflowCheckpoint: new(
                LibraryWorkflowPhase.Completed,
                state.GenerationId,
                new(1),
                LibraryWorkflowPolicyVersions.Current,
                DateTimeOffset.UnixEpoch,
                new(state.GenerationId, new(0))));

        Action act = () => PrepareQuarantineReviewUseCase.Execute(changed, finalAuthors);

        act.Should().Throw<InvalidOperationException>();
    }

    private static FinalAuthorReviewWorkspace FinalAuthors(
        LibraryState state,
        IEnumerable<FinalAuthorReviewSubject>? subjects = null)
    {
        FinalAuthorReviewSubject[] values = subjects?.ToArray() ??
            PrepareFinalAuthorReviewUseCase.Execute(state).Subjects
                .Select(value => value.Apply ? value.WithApply(false) : value)
                .ToArray();
        return new(
            state.Snapshot.Identity.LibraryRoot,
            state.GenerationId,
            state.Revision,
            values,
            state.Snapshot.Books.Count,
            state.Snapshot.Books.Count - values.Length,
            0,
            false);
    }

    private static FinalAuthorReviewSubject FinalSubject(
        CalibreBook book,
        AuthorResolution resolution) => new(
        book.Id,
        book.Title,
        book.Authors,
        [],
        resolution,
        FinalAuthorEvidenceCoverage.None,
        needsInspection: false);

    private static LibraryState State(IEnumerable<CalibreBook> books)
    {
        LibrarySnapshot snapshot = new(
            new("library", 27, "C:\\Libraries\\Source"),
            DateTimeOffset.UnixEpoch,
            books,
            []);
        return new(
            Generation,
            new(0),
            LibraryStateStatus.Authoritative,
            snapshot,
            DateTimeOffset.UnixEpoch,
            workflowCheckpoint: new(
                LibraryWorkflowPhase.Completed,
                Generation,
                new(0),
                LibraryWorkflowPolicyVersions.Current,
                DateTimeOffset.UnixEpoch,
                new(Generation, new(0))));
    }

    private static CalibreBook Book(int id, string title, string author) => new(
        new(id),
        title,
        author,
        [new(new(id), author, author)],
        [],
        [],
        $"{author}/{title} ({id})");
}
