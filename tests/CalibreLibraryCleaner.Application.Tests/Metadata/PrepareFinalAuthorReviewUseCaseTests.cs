using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Executions;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Metadata;

public sealed class PrepareFinalAuthorReviewUseCaseTests
{
    private static readonly FormatFileFingerprint EpubFingerprint = Fingerprint('a', 10);
    private static readonly FormatFileFingerprint PdfFingerprint = Fingerprint('b', 20);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntrinsicMetadataSurvivesVerifiedRenamingWithoutRequiringAnotherParse(bool pdf)
    {
        FormatFileFingerprint fingerprint = pdf ? PdfFingerprint : EpubFingerprint;
        BookFormat renamed = new(pdf ? "PDF" : "EPUB", "renamed", "Current/Book (1)/renamed",
            FormatFileStatus.Present, fingerprint,
            new(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch.AddDays(1), DateTimeOffset.UnixEpoch.AddDays(2), 32));
        CalibreBook book = Book(1, "Unknown", [renamed]);
        LibraryState state = State([book],
            pdf ? [] : [Epub(1, "Previous/book.epub", fingerprint, "Reliable Writer")],
            pdf ? [Pdf(1, "Previous/book.pdf", fingerprint, "Reliable Writer")] : []);

        FinalAuthorReviewWorkspace result = PrepareFinalAuthorReviewUseCase.Execute(state);

        result.Subjects.Single().Resolution.ProposedDisplayNames.Should().Equal("Reliable Writer");
        result.Subjects.Single().NeedsInspection.Should().BeFalse();
        result.Subjects.Single().Apply.Should().BeFalse();
    }

    [Theory]
    [InlineData("analyzer")]
    [InlineData("scoring")]
    [InlineData("metadata-facet")]
    public void StaleOrIncompleteAssessmentMetadataIsNotReused(string difference)
    {
        CalibreBook book = Book(1, "Unknown", [Format("EPUB", "Current/book.epub", EpubFingerprint)]);
        EpubAssessment assessment = new(new FormatAssessment(new(1), "EPUB", "Previous/book.epub", EpubFingerprint,
            AssessmentStatus.Unassessed, null,
            difference == "analyzer" ? new("legacy/0") : EpubAssessmentEngine.AnalyzerVersion,
            difference == "scoring" ? new("legacy/0") : EpubAssessmentEngine.ScoringModelVersion, [Finding()]),
            new(true, true, authors: ["Incompatible Writer"],
                availableFacets: difference == "metadata-facet" ? EpubAssessmentFacet.Package : EpubAssessmentFacet.Metadata));

        FinalAuthorReviewWorkspace result = PrepareFinalAuthorReviewUseCase.Execute(State([book], [assessment]));

        result.Subjects.Single().NeedsInspection.Should().BeTrue();
        result.Subjects.Single().Resolution.ProposedDisplayNames.Should().BeEmpty();
        result.CompatibleEvidenceCount.Should().Be(0);
    }

    [Fact]
    public void PseudonymAnnotationsAppearInExistingFinalReviewWithoutInspectionOrDefaultApply()
    {
        CalibreBook annotated = Book(1, "Morgan Vale (pseud. van Alex Writer)");
        CalibreBook ordinary = Book(2, "Reliable Writer");

        FinalAuthorReviewWorkspace workspace = PrepareFinalAuthorReviewUseCase.Execute(State([annotated, ordinary]));

        FinalAuthorReviewSubject subject = workspace.Subjects.Should().ContainSingle().Subject;
        subject.Resolution.ProposalSource.Should().Be(AuthorProposalSource.PseudonymAnnotationRemoval);
        subject.Resolution.ProposedDisplayNames.Should().Equal("Morgan Vale");
        subject.Apply.Should().BeFalse();
        subject.CanApply.Should().BeTrue();
        subject.NeedsInspection.Should().BeFalse();
        workspace.InspectionDemandCount.Should().Be(0);
        workspace.KnownCount.Should().Be(1);
        annotated.Authors.Single().Name.Should().Be("Morgan Vale (pseud. van Alex Writer)");
    }

    [Fact]
    public void SuspectReviewDoesNotRequireLegacyNormalizationCandidates()
    {
        LibraryState state = State(
            [Book(1, "Known Writer"), Book(2, "Unknown")]);

        FinalAuthorReviewWorkspace workspace = PrepareFinalAuthorReviewUseCase.Execute(state);

        workspace.ScreenedCount.Should().Be(2);
        workspace.KnownCount.Should().Be(1);
        workspace.Subjects.Should().ContainSingle().Which.BookId.Should().Be(new CalibreBookId(2));
        workspace.UnresolvedCount.Should().Be(1);
        workspace.InspectionDemandCount.Should().Be(1);
        workspace.AppliedOnlineProvenanceAvailable.Should().BeFalse();
    }

    [Fact]
    public void CompatibleEpubAndPdfMetadataProduceOneUncheckedProposal()
    {
        CalibreBook book = Book(
            1,
            "Unknown",
            [Format("EPUB", "book.epub", EpubFingerprint), Format("PDF", "book.pdf", PdfFingerprint)]);
        LibraryState state = State(
            [book],
            [Epub(1, "book.epub", EpubFingerprint, "Reliable Writer")],
            [Pdf(1, "book.pdf", PdfFingerprint, "Reliable Writer")]);

        FinalAuthorReviewWorkspace workspace = PrepareFinalAuthorReviewUseCase.Execute(state);

        FinalAuthorReviewSubject subject = workspace.Subjects.Should().ContainSingle().Subject;
        subject.Resolution.ProposalSource.Should().Be(AuthorProposalSource.CachedEmbeddedMetadata);
        subject.Resolution.ProposedDisplayNames.Should().Equal("Reliable Writer");
        subject.Resolution.SelectedByDefault.Should().BeFalse();
        subject.EvidenceCoverage.Should().Be(
            FinalAuthorEvidenceCoverage.EpubPackageMetadata
            | FinalAuthorEvidenceCoverage.PdfDocumentMetadata);
        subject.NeedsInspection.Should().BeFalse();
        workspace.CompatibleEvidenceCount.Should().Be(2);
    }

    [Fact]
    public void StaleAssessmentFingerprintIsIgnoredAndInspectionIsDemanded()
    {
        CalibreBook book = Book(
            1,
            "Unknown",
            [Format("EPUB", "book.epub", EpubFingerprint)]);
        LibraryState state = State(
            [book],
            [Epub(1, "book.epub", Fingerprint('c', 10), "Reliable Writer")]);

        FinalAuthorReviewWorkspace workspace = PrepareFinalAuthorReviewUseCase.Execute(state);

        FinalAuthorReviewSubject subject = workspace.Subjects.Should().ContainSingle().Subject;
        subject.EvidenceCoverage.Should().Be(FinalAuthorEvidenceCoverage.None);
        subject.Resolution.ProposalSource.Should().Be(AuthorProposalSource.None);
        subject.NeedsInspection.Should().BeTrue();
        workspace.CompatibleEvidenceCount.Should().Be(0);
    }

    [Fact]
    public void CredibleCurrentMetadataDoesNotConsultLowerPriorityCachedEvidence()
    {
        CalibreBook book = Book(
            1,
            "Reliable Writer",
            [Format("EPUB", "book.epub", EpubFingerprint)]);
        LibraryState state = State(
            [book],
            [Epub(1, "book.epub", EpubFingerprint, "Unknown")]);

        FinalAuthorReviewWorkspace workspace = PrepareFinalAuthorReviewUseCase.Execute(state);

        workspace.KnownCount.Should().Be(1);
        workspace.Subjects.Should().BeEmpty();
        workspace.CompatibleEvidenceCount.Should().Be(0);
    }

    [Fact]
    public void VerifiedAppliedOnlineAuthorIsProtectedFromSuspicionAfterReload()
    {
        string author = "1922 Reliable Writer";
        AppliedAuthorProvenance provenance = new(
            "provider",
            "provider/1.0.0",
            "edition",
            "proposal/1.0.0",
            AppliedAuthorProvenance.CreateValuesIdentity([author]));
        CalibreBook book = new(
            new(1),
            "The Book",
            author,
            [new(new(1), author, author)],
            [],
            [],
            "Author/Book (1)",
            appliedAuthorProvenance: provenance);

        FinalAuthorReviewWorkspace workspace = PrepareFinalAuthorReviewUseCase.Execute(State([book]));

        workspace.KnownCount.Should().Be(1);
        workspace.Subjects.Should().BeEmpty();
        workspace.AppliedOnlineProvenanceAvailable.Should().BeTrue();
    }

    [Fact]
    public void ConservativeNameOrderRemainsDefaultSelected()
    {
        CalibreBook book = new(
            new(1),
            "The Book",
            "Writer, Reliable",
            [new(new(1), "Writer| Reliable", "Writer, Reliable")],
            [],
            [],
            "Writer/Book (1)");

        FinalAuthorReviewSubject subject = PrepareFinalAuthorReviewUseCase.Execute(State([book]))
            .Subjects.Should().ContainSingle().Subject;

        subject.Resolution.ProposalSource.Should().Be(AuthorProposalSource.ConservativeNameOrder);
        subject.Resolution.SelectedByDefault.Should().BeTrue();
        subject.NeedsInspection.Should().BeFalse();
    }

    [Fact]
    public void PipeLifeDatesProduceCheckedCanonicalAuthor()
    {
        CalibreBook book = new(
            new(1),
            "The Book",
            "Kipling, Rudyard, 1865-1936",
            [new(new(1), "Kipling| Rudyard| 1865-1936", "Kipling, Rudyard, 1865-1936")],
            [],
            [],
            "Kipling/Book (1)");

        FinalAuthorReviewSubject subject = PrepareFinalAuthorReviewUseCase.Execute(State([book]))
            .Subjects.Should().ContainSingle().Subject;

        subject.Resolution.ProposalSource.Should().Be(AuthorProposalSource.ConservativeNameOrder);
        subject.Resolution.ProposedDisplayNames.Should().Equal("Rudyard Kipling");
        subject.Resolution.ProposedSortNames.Should().Equal("Kipling, Rudyard");
        subject.Apply.Should().BeTrue();
        subject.NeedsInspection.Should().BeFalse();
    }

    [Fact]
    public void DeterministicTypographyIsCheckedWithoutEvidenceInspection()
    {
        CalibreBook book = new(
            new(1),
            "The Book",
            "Rowling, J.K.",
            [new(new(1), "J.K. Rowling", "Rowling, J.K.")],
            [],
            [],
            "J.K. Rowling/Book (1)");

        FinalAuthorReviewSubject subject = PrepareFinalAuthorReviewUseCase.Execute(State([book]))
            .Subjects.Should().ContainSingle().Subject;

        subject.Resolution.ProposalSource.Should().Be(AuthorProposalSource.DeterministicTypography);
        subject.Resolution.ProposedDisplayNames.Should().Equal("J. K. Rowling");
        subject.Resolution.ProposedSortNames.Should().Equal("Rowling, J. K.");
        subject.Apply.Should().BeTrue();
        subject.NeedsInspection.Should().BeFalse();
    }

    [Fact]
    public void FrontMatterAuthorAndRolesRemainVisibleAndUnchecked()
    {
        CalibreBook book = Book(1, "Unknown", [Format("EPUB", "book.epub", EpubFingerprint)]);
        FrontMatterAuthorObservation observation = new(
            "EPUB",
            EpubFingerprint,
            FrontMatterAuthorVersions.EpubExtractor,
            FrontMatterAuthorVersions.Limits,
            FrontMatterAuthorStatus.Available,
            [
                new(AuthorCreditRole.Author, ["Reliable Writer"], 1, 2, "By"),
                new(AuthorCreditRole.Editor, ["Editorial Person"], 2, 4, "Edited by"),
            ],
            3,
            3,
            1_000,
            false);

        FinalAuthorReviewSubject subject = PrepareFinalAuthorReviewUseCase.Execute(
            State([book]), frontMatterObservations: [observation])
            .Subjects.Should().ContainSingle().Subject;

        subject.Resolution.ProposalSource.Should().Be(AuthorProposalSource.ContentDerivedFrontMatter);
        subject.Resolution.SelectedByDefault.Should().BeFalse();
        subject.FrontMatterEvidence.Single().Candidates.Should().HaveCount(2);
        subject.NeedsInspection.Should().BeFalse();
    }

    [Fact]
    public void UnavailableFrontMatterRemainsIncompleteWithoutInventingAnAuthor()
    {
        CalibreBook book = Book(1, "Unknown", [Format("PDF", "book.pdf", PdfFingerprint)]);
        FrontMatterAuthorObservation observation = new(
            "PDF",
            PdfFingerprint,
            FrontMatterAuthorVersions.PdfExtractor,
            FrontMatterAuthorVersions.Limits,
            FrontMatterAuthorStatus.Unavailable,
            [],
            0,
            0,
            0,
            false,
            "PDF_FRONT_MATTER.PASSWORD_REQUIRED");

        FinalAuthorReviewSubject subject = PrepareFinalAuthorReviewUseCase.Execute(
            State([book]), frontMatterObservations: [observation])
            .Subjects.Should().ContainSingle().Subject;

        subject.Resolution.ProposalSource.Should().Be(AuthorProposalSource.None);
        subject.Resolution.Status.Should().Be(AuthorResolutionStatus.Unresolved);
        subject.NeedsInspection.Should().BeTrue();
        subject.FrontMatterEvidence.Single().ProblemCode.Should().Be("PDF_FRONT_MATTER.PASSWORD_REQUIRED");
    }

    [Fact]
    public void NonCompletedStateCannotPrepareFinalReview()
    {
        LibraryState state = State([Book(1, "Unknown")], phase: LibraryWorkflowPhase.CandidateCleanupCompleted);

        Action act = () => PrepareFinalAuthorReviewUseCase.Execute(state);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*completed authoritative workflow state*");
    }

    private static LibraryState State(
        IEnumerable<CalibreBook> books,
        IEnumerable<EpubAssessment>? epubs = null,
        IEnumerable<PdfAssessment>? pdfs = null,
        LibraryWorkflowPhase phase = LibraryWorkflowPhase.Completed)
    {
        LibrarySnapshot snapshot = new(
            new("library", 27, "C:\\library"),
            DateTimeOffset.UnixEpoch,
            books,
            [],
            epubAssessments: epubs,
            pdfAssessments: pdfs);
        LibraryStateGenerationId generation = new(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
        return new(
            generation,
            new(0),
            LibraryStateStatus.Authoritative,
            snapshot,
            DateTimeOffset.UnixEpoch,
            workflowCheckpoint: new(
                phase,
                generation,
                new(0),
                LibraryWorkflowPolicyVersions.Current,
                DateTimeOffset.UnixEpoch,
                new(generation, new(0))));
    }

    private static CalibreBook Book(
        int id,
        string author,
        IEnumerable<BookFormat>? formats = null) => new(
        new(id),
        "The Book",
        author,
        [new(new(id), author, author)],
        [],
        formats ?? [],
        $"Author/Book ({id})");

    private static BookFormat Format(
        string format,
        string path,
        FormatFileFingerprint fingerprint) => new(
        format,
        path,
        path,
        FormatFileStatus.Present,
        fingerprint,
        Observation(fingerprint));

    private static EpubAssessment Epub(
        int id,
        string path,
        FormatFileFingerprint fingerprint,
        string author) => new(
        new FormatAssessment(
            new(id),
            "EPUB",
            path,
            fingerprint,
            AssessmentStatus.Unassessed,
            null,
            EpubAssessmentEngine.AnalyzerVersion,
            EpubAssessmentEngine.ScoringModelVersion,
            [Finding()]),
        new EpubFeatureSummary(
            opened: true,
            packageParsed: true,
            authors: [author],
            coverage: EpubAssessmentCoverage.Incomplete,
            availableFacets: EpubAssessmentFacet.Metadata));

    private static PdfAssessment Pdf(
        int id,
        string path,
        FormatFileFingerprint fingerprint,
        string author)
    {
        FormatFileObservation observation = Observation(fingerprint);
        FormatAssessment result = new(
            new(id),
            "PDF",
            path,
            fingerprint,
            AssessmentStatus.Unassessed,
            null,
            PdfAssessmentEngine.AnalyzerVersion,
            PdfAssessmentEngine.ScoringModelVersion,
            [Finding()],
            PdfAssessment.V1Components,
            observation);
        PdfDocumentMetadataSummary metadata = PdfDocumentMetadataSummary.Empty with
        {
            Author = new(PdfMetadataValueStatus.Present, author),
        };
        PdfFeatureSummary features = new(
            PdfOpenStatus.Opened,
            PdfEncryptionStatus.NotEncrypted,
            1,
            "1.7",
            metadata,
            new(PdfTextExtractionStatus.Available, 1, 1, 1, 500, 10_000, PdfTextDensityBand.Useful, false),
            new(1, 0, 0, 0, 0, PdfContentBalance.TextHeavy, false),
            false,
            0,
            new(0, 0, 0),
            new(1, 1, 0, 0, 0, 0),
            [],
            [],
            PdfDocumentClassification.DigitalText,
            PdfClassificationConfidence.High,
            new(PdfSamplingMode.AllPages, 1, [1], 1, new("pdf-sampling/test")),
            new("pdf-classification/test"),
            PdfAssessmentEngine.ResourceProfileVersion);
        return new(result, features);
    }

    private static AssessmentFinding Finding() => new(
        "TEST.AUTHOR.EVIDENCE",
        FindingSeverity.Information,
        0,
        "Synthetic author evidence.");

    private static FormatFileFingerprint Fingerprint(char value, long size) => new(
        size,
        new(new string(value, 64)));

    private static FormatFileObservation Observation(FormatFileFingerprint fingerprint) => new(
        fingerprint.SizeInBytes,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        0);
}
