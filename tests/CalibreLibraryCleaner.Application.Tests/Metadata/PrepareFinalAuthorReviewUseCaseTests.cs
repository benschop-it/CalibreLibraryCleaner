using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Assessments;
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
    public void ConservativeNameOrderRemainsTheOnlyDefaultSelectedProposal()
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
