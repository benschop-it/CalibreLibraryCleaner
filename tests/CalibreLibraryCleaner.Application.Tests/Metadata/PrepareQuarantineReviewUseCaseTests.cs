using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Metadata;

public sealed class PrepareQuarantineReviewUseCaseTests
{
    [Fact]
    public void UnavailableLegacyCoauthorOrderDoesNotCountAsExhaustedResolution()
    {
        CalibreBook book = Book(1, "The Book", "Unknown");
        LibraryState state = State([book]);
        EditionMetadataProviderIdentity provider = new("open-library", "provider/1");
        EditionMetadataCandidate candidate = new("work", "edition", book.Title,
            ["Kemp, Kristen", "Lannert, Stacy"], authorOrderKnown: false);
        EditionMetadataProposal proposal = new(book.Id, provider, EditionMetadataQueryFields.Identifier,
            DateTimeOffset.UnixEpoch, EditionMetadataProposalStatus.Proposed, candidate, 10_001,
            ["METADATA.EDITION.ISBN_EXACT"]);
        EditionMetadataProvidersBatchResult results = new(
            [new(provider, new Dictionary<CalibreBookId, EditionMetadataProposal> { [book.Id] = proposal },
                1, 1, 0, 1, 0, 1, 0, false, true)],
            new Dictionary<CalibreBookId, FinalRecordInputIdentity> { [book.Id] = FinalRecordInputIdentity.Create(book) });

        QuarantineReviewWorkspace review = PrepareQuarantineReviewUseCase.Execute(
            state, FinalAuthors(state), providerResults: results);

        QuarantineReviewSubject subject = review.Subjects.Should().ContainSingle().Subject;
        subject.Include.Should().BeFalse();
        subject.Disposition.Qualification.Should().Be(FinalRecordQualification.NeedsReview);
        subject.Disposition.ReasonCodes.Should().Contain(EditionMetadataAuthorWritePolicy.LegacyAuthorOrderUnavailable);
        subject.Disposition.Evidence!.Routes.Should().Contain(value =>
            value.Name == provider.Id && value.Outcome == FinalRecordRouteOutcome.Unavailable);
        subject.Disposition.Evidence.ResolutionExhausted.Should().BeFalse();
    }

    private static readonly LibraryStateGenerationId Generation = new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    [Fact]
    public void CompleteInputIdentityIsStructuralAndDoesNotPrintBibliographicValues()
    {
        CalibreBook first = Book(1, "Private Title", "Private Writer");
        CalibreBook same = Book(1, "Private Title", "Private Writer");
        CalibreBook changed = Book(1, "Private Title Changed", "Private Writer");

        FinalRecordInputIdentity.Create(first).Should().Be(FinalRecordInputIdentity.Create(same));
        FinalRecordInputIdentity.Create(first).Should().NotBe(FinalRecordInputIdentity.Create(changed));
        FinalRecordInputIdentity.Create(first).ToString().Should().NotContain("Private");
    }

    [Fact]
    public void ProviderEvidenceFromChangedMetadataCannotBeReusedAtTheSameRevision()
    {
        CalibreBook before = Book(1, "Old Title", "Reliable Writer");
        LibraryState current = State([Book(1, "Changed Title", "Reliable Writer")]);
        EditionMetadataProvidersBatchResult stale = new(
            [], new Dictionary<CalibreBookId, FinalRecordInputIdentity>
            {
                [before.Id] = FinalRecordInputIdentity.Create(before),
            });

        Action prepare = () => PrepareQuarantineReviewUseCase.Execute(
            current, FinalAuthors(current), providerResults: stale);

        prepare.Should().Throw<InvalidOperationException>().WithMessage("*inputs changed*");
    }

    [Fact]
    public void MissingAuthorSubjectDoesNotAuthorizeTransferOfSuspiciousTitle()
    {
        LibraryState state = State([Book(1, "Unknown", "Reliable Writer")]);

        QuarantineReviewWorkspace workspace = PrepareQuarantineReviewUseCase.Execute(
            state, FinalAuthors(state, []));

        workspace.Subjects.Where(value => value.Include).Should().BeEmpty(
            "a label without completed resolution routes is not exhausted evidence");
    }

    [Fact]
    public void UnresolvedAuthorWithoutCompletedInspectionRemainsPending()
    {
        LibraryState state = State([Book(1, "The Book", "Unknown")]);

        QuarantineReviewWorkspace workspace = PrepareQuarantineReviewUseCase.Execute(
            state, FinalAuthors(state));

        workspace.Subjects.Where(value => value.Include).Should().BeEmpty(
            "missing proposals and missing inspection are not completed negative evidence");
    }

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
        workspace.Subjects.Select(value => value.BookId.Value).Should().Equal(1, 2, 3);
        workspace.Subjects.Should().OnlyContain(value => !value.Include);
        workspace.RetainedInputCount.Should().Be(3);
        workspace.PendingCount.Should().Be(3);
        workspace.IsHighQualityComplete.Should().BeFalse();
    }

    [Fact]
    public void FinalConflictingRowsRemainPendingWithoutExhaustedEvidence()
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

        quarantine.Include.Should().BeFalse();
        quarantine.Disposition.Qualification.Should().Be(FinalRecordQualification.NeedsReview);
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

    [Fact]
    public void EveryRetainedTitleIsTriagedIndependentlyOfAuthorSubjects()
    {
        LibraryState state = State([Book(1, "(1997 The Silent Valley", "Reliable Writer")]);

        QuarantineReviewWorkspace workspace = PrepareQuarantineReviewUseCase.Execute(
            state, FinalAuthors(state, []));

        workspace.Subjects.Should().ContainSingle().Which.Disposition.ReasonCodes
            .Should().Contain("TITLE.UNBALANCED_DELIMITER");
        workspace.PendingCount.Should().Be(1);
    }

    [Fact]
    public void ManyUnsupportedFormatsStayBoundedAndPendingRatherThanFailingTheCompletePass()
    {
        CalibreBook ordinary = Book(1, "A Book", "Reliable Writer");
        CalibreBook book = new(
            ordinary.Id, ordinary.Title, ordinary.AuthorSort, ordinary.Authors, [],
            Enumerable.Range(1, 40).Select(index => new BookFormat(
                $"X{index}", "book", $"Writer/Book (1)/book.x{index}", FormatFileStatus.Present,
                ordinary.Formats[0].Fingerprint, ordinary.Formats[0].Observation)), ordinary.RelativeDirectory);
        LibraryState state = State([book]);

        QuarantineReviewWorkspace workspace = PrepareQuarantineReviewUseCase.Execute(state, FinalAuthors(state));

        workspace.Subjects.Should().ContainSingle().Which.Disposition.Evidence!.Routes.Should().HaveCount(2);
        workspace.PendingCount.Should().Be(1);
        workspace.Subjects[0].Include.Should().BeFalse();
    }

    [Fact]
    public void ReviewExclusionDoesNotQualifyAndCheckAllDoesNotAssignPendingRecords()
    {
        LibraryState state = State([Book(1, "The Book", "Unknown")]);
        QuarantineReviewWorkspace workspace = PrepareQuarantineReviewUseCase.Execute(
            state, FinalAuthors(state));

        workspace = PrepareQuarantineReviewUseCase.SetAllInclude(workspace, true);
        workspace.Subjects.Should().OnlyContain(value => !value.Include);
        workspace = PrepareQuarantineReviewUseCase.IncludeManually(state, workspace, new(1));
        workspace = PrepareQuarantineReviewUseCase.SetAllInclude(workspace, false);

        workspace.IsHighQualityComplete.Should().BeFalse();
        workspace.PendingCount.Should().Be(1);
    }

    [Fact]
    public void ExplicitAcceptanceAndVerifiedMovesAccountForEachRetainedRecordExactlyOnce()
    {
        LibraryState state = State([
            Book(1, "The Book", "Reliable Writer"), Book(2, "Unknown", "Unknown")]);
        QuarantineReviewWorkspace workspace = PrepareQuarantineReviewUseCase.Execute(
            state, FinalAuthors(state), managedLayoutVerified: true);
        workspace = PrepareQuarantineReviewUseCase.AcceptReviewed(state, workspace, [new(1)]);
        workspace = PrepareQuarantineReviewUseCase.IncludeManually(state, workspace, new(2));

        workspace = PrepareQuarantineReviewUseCase.RecordVerifiedMoves(workspace, [new(2)]);

        workspace.RetainedInputCount.Should().Be(2);
        workspace.QualifiedCount.Should().Be(1);
        workspace.VerifiedMovedCount.Should().Be(1);
        workspace.PendingCount.Should().Be(0);
        workspace.IsHighQualityComplete.Should().BeTrue();
    }

    [Fact]
    public void IndependentOpeningIdentityUsableContentAndVerifiedLayoutQualifyWithoutProvider()
    {
        CalibreBook book = Book(1, "The Northern Valley", "Reliable Writer");
        EpubAssessment assessment = new(
            book.Id, "EPUB", book.Formats[0].ExpectedRelativePath, book.Formats[0].Fingerprint,
            AssessmentStatus.Completed, new(0),
            EpubAssessmentEngine.AnalyzerVersion, EpubAssessmentEngine.ScoringModelVersion,
            new(opened: true, packageParsed: true, readableCharacterCount: 1_000),
            [new("FIXTURE.READABLE", FindingSeverity.Information, 0, "Generated readable content.")]);
        LibraryState state = State([book], [assessment]);
        FrontMatterAuthorObservation local = new(
            "EPUB", book.Formats[0].Fingerprint!, FrontMatterAuthorVersions.EpubExtractor,
            FrontMatterAuthorVersions.Limits, FrontMatterAuthorStatus.Available,
            [new(AuthorCreditRole.Author, ["Reliable Writer"], 1, 2, "By")], 1, 1, 100, false,
            identityCandidates: [new(book.Title, ["Reliable Writer"], 1, 1, 2, true)]);
        FinalAuthorReviewWorkspace authors = new(
            state.Snapshot.Identity.LibraryRoot, state.GenerationId, state.Revision, [], 1, 1, 0, false,
            frontMatterObservations: [local]);

        QuarantineReviewWorkspace workspace = PrepareQuarantineReviewUseCase.Execute(
            state, authors, managedLayoutVerified: true);

        workspace.QualifiedCount.Should().Be(1);
        workspace.PendingCount.Should().Be(0);
        workspace.IsHighQualityComplete.Should().BeTrue();
        PrepareQuarantineReviewUseCase.Execute(state, authors, managedLayoutVerified: false)
            .IsHighQualityComplete.Should().BeFalse();
        QuarantineReviewWorkspace skippedNaming = PrepareQuarantineReviewUseCase.Execute(
            state, authors, managedLayoutVerified: true, verifiedManagedNameBookIds: new HashSet<CalibreBookId>());
        skippedNaming.QualifiedCount.Should().Be(0);
        Action acceptSkipped = () => PrepareQuarantineReviewUseCase.AcceptReviewed(
            state, skippedNaming, [book.Id]);
        acceptSkipped.Should().Throw<InvalidOperationException>().WithMessage("*Managed naming*");
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

    private static LibraryState State(
        IEnumerable<CalibreBook> books,
        IEnumerable<EpubAssessment>? assessments = null)
    {
        LibrarySnapshot snapshot = new(
            new("library", 27, "C:\\Libraries\\Source"),
            DateTimeOffset.UnixEpoch,
            books,
            [], epubAssessments: assessments);
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
        [new("EPUB", "book", $"{author}/{title} ({id})/book.epub",
            FormatFileStatus.Present, new(100, new(new string('a', 64))),
            new(100, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))],
        $"{author}/{title} ({id})");
}
