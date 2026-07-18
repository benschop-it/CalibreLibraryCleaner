using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Findings;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recommendations;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Recommendations;

public sealed class ConsolidationRecommendationPolicyTests
{
    private static readonly LibraryIdentity Identity = new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "library");

    [Fact]
    public void ComplementaryFormatsCanComeFromDifferentRecords()
    {
        CalibreBook first = Book(1, [Format("EPUB", 1, "01")], new(languages: ["eng"], hasCover: true));
        CalibreBook second = Book(2, [Format("PDF", 2, "02")], new(languages: ["eng"]));

        ConsolidationRecommendation recommendation = Generate([second, first]);

        recommendation.MetadataSource!.SelectedBookId.Should().Be(first.Id);
        recommendation.FormatSelections.Should().Contain(value => value.Format == "EPUB" && value.ProposedSource!.BookId == first.Id);
        recommendation.FormatSelections.Should().Contain(value => value.Format == "PDF" && value.ProposedSource!.BookId == second.Id);
        recommendation.ProposedRedundantRecords.Should().BeEmpty();
    }

    [Fact]
    public void NonIdenticalUnassessedFormatsRemainUnresolvedAndPreserved()
    {
        CalibreBook first = Book(1, [Format("AZW3", 10, "01")]);
        CalibreBook second = Book(2, [Format("AZW3", 20, "02")]);

        ConsolidationRecommendation recommendation = Generate([first, second]);

        FormatSourceSelection selection = recommendation.FormatSelections.Single();
        selection.ResolutionStatus.Should().Be(FormatResolutionStatus.UnresolvedConflict);
        selection.ProposedSource.Should().BeNull();
        selection.Candidates.Should().HaveCount(2);
        selection.ProposedExcludedAlternatives.Should().BeEmpty();
        recommendation.Confidence.Should().Be(RecommendationConfidence.ManualReviewRequired);
        recommendation.ProposedRedundantRecords.Should().BeEmpty();
    }

    [Fact]
    public void ExactBinaryAlternativesUseMetadataSourceAndCanProposeCoveredRecordAsRedundant()
    {
        FormatFileFingerprint fingerprint = Fingerprint(10, "ab");
        CalibreBook first = Book(1, [Format("AZW3", fingerprint, "one.azw3")], new(hasCover: true));
        CalibreBook second = Book(2, [Format("AZW3", fingerprint, "two.azw3")]);

        ConsolidationRecommendation recommendation = Generate([second, first]);

        FormatSourceSelection format = recommendation.FormatSelections.Single();
        format.ProposedSource!.BookId.Should().Be(first.Id);
        format.ProposedExcludedAlternatives.Should().ContainSingle(value => value.BookId == second.Id);
        recommendation.ProposedRedundantRecords.Should().ContainSingle(value => value.BookId == second.Id);
        recommendation.Reasons.Should().Contain(value => value.Code == "FORMAT.EXACT_BINARY_EQUIVALENT");
    }

    [Theory]
    [InlineData(10, FormatResolutionStatus.Selected)]
    [InlineData(9, FormatResolutionStatus.UnresolvedConflict)]
    public void EpubThresholdAlsoRequiresDecisiveFindings(int scoreGap, FormatResolutionStatus expected)
    {
        CalibreBook first = Book(1, [Format("EPUB", 10, "01")]);
        CalibreBook second = Book(2, [Format("EPUB", 20, "02")]);
        FormatAssessment strong = Assessment(first, 80, 80);
        FormatAssessment weaker = Assessment(second, 80 - scoreGap, 80 - scoreGap);

        ConsolidationRecommendation recommendation = Generate([first, second], [strong, weaker]);

        recommendation.FormatSelections.Single().ResolutionStatus.Should().Be(expected);
        if (expected == FormatResolutionStatus.Selected)
        {
            recommendation.FormatSelections.Single().ProposedSource!.BookId.Should().Be(first.Id);
        }
    }

    [Fact]
    public void LanguageConflictKeepsBothRecordsSeparateAndRequiresReview()
    {
        CalibreBook first = Book(1, [Format("EPUB", 10, "01")], new(languages: ["eng"]));
        CalibreBook second = Book(2, [Format("EPUB", 20, "02")], new(languages: ["deu"]));

        ConsolidationRecommendation recommendation = Generate([first, second]);

        recommendation.RetainedSeparateRecords.Should().HaveCount(2);
        recommendation.MetadataSource.Should().BeNull();
        recommendation.FormatSelections.Should().BeEmpty();
        recommendation.Confidence.Should().Be(RecommendationConfidence.ManualReviewRequired);
        recommendation.Warnings.Should().Contain(value => value.Code == "METADATA.LANGUAGE_CONFLICT");
    }

    [Fact]
    public void OverlappingLanguageSetsAreNotTreatedAsDisjointEditionEvidence()
    {
        CalibreBook first = Book(1, [], new(languages: ["eng"]));
        CalibreBook second = Book(2, [], new(languages: ["eng", "deu"]));

        ConsolidationRecommendation recommendation = Generate([first, second]);

        recommendation.Warnings.Should().NotContain(value => value.Code == "METADATA.LANGUAGE_CONFLICT");
        recommendation.RetainedSeparateRecords.Should().BeEmpty();
    }

    [Fact]
    public void RecordIdTieBreakIsExplicitlyNotQualityEvidence()
    {
        ConsolidationRecommendation recommendation = Generate([Book(9, []), Book(3, [])]);

        recommendation.MetadataSource!.SelectedBookId.Value.Should().Be(3);
        recommendation.Reasons.Single(value => value.Code == "METADATA.TIE_BROKEN_BY_RECORD_ID").Explanation.Should().Contain("not as a quality signal");
    }

    [Fact]
    public void ConflictingValidStrongIdentifiersRequireSeparateReview()
    {
        CalibreBook first = BookWithIdentifiers(1, [new("isbn", "9780306406157")]);
        CalibreBook second = BookWithIdentifiers(2, [new("isbn", "9783161484100")]);

        ConsolidationRecommendation recommendation = Generate([first, second]);

        recommendation.Warnings.Should().Contain(value => value.Code == "IDENTIFIER.STRONG_CONFLICT");
        recommendation.RetainedSeparateRecords.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(2020, 2021, false)]
    [InlineData(2020, 2022, true)]
    public void PublicationYearBoundaryWarnsAndBlocksOnlyAtTwoYears(int firstYear, int secondYear, bool separated)
    {
        CalibreBook first = Book(1, [], new(publicationDate: new DateTimeOffset(firstYear, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        CalibreBook second = Book(2, [], new(publicationDate: new DateTimeOffset(secondYear, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        ConsolidationRecommendation recommendation = Generate([first, second]);

        recommendation.Warnings.Should().Contain(value => value.Code == "METADATA.PUBLICATION_YEAR_CONFLICT");
        recommendation.RetainedSeparateRecords.Any().Should().Be(separated);
    }

    [Fact]
    public void HigherEpubScoreWithoutDecisiveSupportRemainsUnresolved()
    {
        CalibreBook first = Book(1, [Format("EPUB", 10, "01")]);
        CalibreBook second = Book(2, [Format("EPUB", 20, "02")]);
        FormatAssessment strongCoverOnly = Assessment(first, 80, 80, "EPUB.COVER.PRESENT");
        FormatAssessment weakerCoverOnly = Assessment(second, 60, 60, "EPUB.COVER.PRESENT");

        ConsolidationRecommendation recommendation = Generate([first, second], [strongCoverOnly, weakerCoverOnly]);

        recommendation.FormatSelections.Single().ResolutionStatus.Should().Be(FormatResolutionStatus.UnresolvedConflict);
    }

    [Fact]
    public void CompletedEpubIsPreferredOverDecisivelyDisqualifiedAlternative()
    {
        CalibreBook first = Book(1, [Format("EPUB", 10, "01")]);
        CalibreBook second = Book(2, [Format("EPUB", 20, "02")]);
        FormatAssessment completed = Assessment(first, 70, 70);
        BookFormat failedFormat = second.Formats.Single();
        FormatAssessment failed = new(
            second.Id,
            "EPUB",
            failedFormat.ExpectedRelativePath,
            failedFormat.Fingerprint,
            AssessmentStatus.Disqualified,
            null,
            new("epub-inspector/1.0.1"),
            new("epub-quality/1.0.0"),
            new(false, false),
            [new AssessmentFinding("EPUB.OPEN", FindingSeverity.Disqualifying, 0, "Synthetic open failure.")]);

        ConsolidationRecommendation recommendation = Generate([first, second], [completed, failed]);

        recommendation.FormatSelections.Single().ProposedSource!.BookId.Should().Be(first.Id);
        recommendation.Reasons.Should().Contain(value => value.Code == "EPUB.VALID_OVER_DISQUALIFIED");
    }

    [Fact]
    public void PlaceholderOnlyMetadataProducesUnsupportedVisibleShell()
    {
        CalibreBook first = new(new(1), "Unknown", "Unknown", [new(new(1), "Unknown Author", "Unknown")], [], [], "one");
        CalibreBook second = new(new(2), "UNKNOWN", "Unknown", [new(new(2), "UNKNOWN AUTHOR", "Unknown")], [], [], "two");

        ConsolidationRecommendation recommendation = Generate([first, second]);

        recommendation.Confidence.Should().Be(RecommendationConfidence.Unsupported);
        recommendation.MetadataSource.Should().BeNull();
        recommendation.Warnings.Should().Contain(value => value.Severity == RecommendationWarningSeverity.Blocking);
    }

    [Fact]
    public void InconsistentExactBinaryFingerprintProducesUnsupportedShell()
    {
        CalibreBook first = Book(1, [Format("PDF", 10, "01")]);
        CalibreBook second = Book(2, [Format("PDF", 20, "02")]);
        CalibreBook[] books = [first, second];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        FormatFileFingerprint claimed = Fingerprint(99, "ff");
        ExactBinaryDuplicateGroup inconsistent = new(
            ExactBinaryDuplicateGroupId.From(claimed),
            claimed,
            [
                new(first.Id, "PDF", first.Formats.Single().ExpectedRelativePath),
                new(second.Id, "PDF", second.Formats.Single().ExpectedRelativePath),
            ]);

        ConsolidationRecommendation recommendation = new ConsolidationRecommendationPolicy().Generate(
            Identity, group, books, [inconsistent], [], [], CancellationToken.None);

        recommendation.Confidence.Should().Be(RecommendationConfidence.Unsupported);
        recommendation.FormatSelections.Should().BeEmpty();
        recommendation.ProposedRedundantRecords.Should().BeEmpty();
        recommendation.Warnings.Should().ContainSingle(value => value.Severity == RecommendationWarningSeverity.Blocking);
    }

    [Fact]
    public void NonDecisiveEpubDisqualificationDoesNotSelectWinner()
    {
        CalibreBook first = Book(1, [Format("EPUB", 10, "01")]);
        CalibreBook second = Book(2, [Format("EPUB", 20, "02")]);
        FormatAssessment completed = Assessment(first, 70, 70);
        BookFormat failedFormat = second.Formats.Single();
        FormatAssessment failed = new(
            second.Id,
            "EPUB",
            failedFormat.ExpectedRelativePath,
            failedFormat.Fingerprint,
            AssessmentStatus.Disqualified,
            null,
            new("epub-inspector/1.0.1"),
            new("epub-quality/1.0.0"),
            new(false, false),
            [new AssessmentFinding("EPUB.ENCRYPTION", FindingSeverity.Disqualifying, 0, "Synthetic encryption finding.")]);

        ConsolidationRecommendation recommendation = Generate([first, second], [completed, failed]);

        recommendation.FormatSelections.Single().ResolutionStatus.Should().Be(FormatResolutionStatus.UnresolvedConflict);
        recommendation.FormatSelections.Single().ProposedSource.Should().BeNull();
    }

    [Fact]
    public void EpubPreferenceExposesDecisiveEvidence()
    {
        CalibreBook first = Book(1, [Format("EPUB", 10, "01")]);
        CalibreBook second = Book(2, [Format("EPUB", 20, "02")]);

        ConsolidationRecommendation recommendation = Generate(
            [first, second],
            [Assessment(first, 80, 80), Assessment(second, 60, 60)]);

        RecommendationReason reason = recommendation.Reasons.Single(value => value.Code == "EPUB.MATERIAL_QUALITY_ADVANTAGE");
        reason.Evidence.Should().ContainKey("record.1.decisiveFindings");
        reason.Evidence["record.1.decisiveFindings"].Should().Contain("EPUB.NAVIGATION");
        reason.Evidence.Should().ContainKey("record.2.score").WhoseValue.Should().Be("60");
    }

    [Fact]
    public void MetadataConsensusConflictPrecedesCompleteness()
    {
        CalibreBook outlier = Book(1, [], new(publisher: "Outlier", languages: ["eng"], hasCover: true));
        CalibreBook consensus1 = Book(2, [], new(publisher: "Consensus", languages: ["eng"]));
        CalibreBook consensus2 = Book(3, [], new(publisher: "Consensus", languages: ["eng"]));

        ConsolidationRecommendation recommendation = Generate([outlier, consensus1, consensus2]);

        recommendation.MetadataSource!.SelectedBookId.Should().Be(consensus1.Id);
        recommendation.MetadataSource.Comparisons.Single(value => value.BookId == outlier.Id).Vector.ConflictCount.Should().Be(1);
    }

    [Fact]
    public void InvalidIdentifierAndDifferentFormatSetsLowerConfidenceWithWarnings()
    {
        CalibreBook first = new(new(1), "Shared Book", "Author", [new(new(1), "Shared Author", "Author")], [new("isbn", "invalid")], [Format("EPUB", 10, "01")], "one");
        CalibreBook second = new(new(2), "Shared Book", "Author", [new(new(2), "Shared Author", "Author")], [], [Format("PDF", 20, "02")], "two");

        ConsolidationRecommendation recommendation = Generate([first, second]);

        recommendation.Warnings.Should().Contain(value => value.Code == "IDENTIFIER.STRONG_INVALID" && value.BookId == first.Id);
        recommendation.Warnings.Should().Contain(value => value.Code == "FORMAT.SUBSTANTIALLY_DIFFERENT_AVAILABILITY");
        recommendation.Confidence.Should().Be(RecommendationConfidence.Low);
    }

    [Fact]
    public void IdenticalFormatDoesNotHideAnotherUniqueFormatOrMarkItsRecordRedundant()
    {
        FormatFileFingerprint shared = Fingerprint(10, "ab");
        CalibreBook first = Book(1, [Format("AZW3", shared, "one.azw3")], new(languages: ["eng"]));
        CalibreBook second = Book(2, [
            Format("AZW3", shared, "two.azw3"),
            Format("PDF", Fingerprint(20, "cd"), "unique.pdf"),
        ], new(languages: ["eng"]));

        ConsolidationRecommendation recommendation = Generate([first, second]);

        recommendation.FormatSelections.Single(value => value.Format == "PDF").ProposedSource!.BookId.Should().Be(second.Id);
        recommendation.ProposedRedundantRecords.Should().BeEmpty();
    }

    [Fact]
    public void UnavailableFormatBlocksWholeRecordRedundancy()
    {
        FormatFileFingerprint shared = Fingerprint(10, "ab");
        CalibreBook first = Book(1, [Format("AZW3", shared, "one.azw3")], new(languages: ["eng"]));
        CalibreBook second = Book(2, [
            Format("AZW3", shared, "two.azw3"),
            new BookFormat("PDF", "missing", "missing.pdf", FormatFileStatus.Missing),
        ], new(languages: ["eng"]));

        ConsolidationRecommendation recommendation = Generate([first, second]);

        recommendation.FormatSelections.Single(value => value.Format == "PDF").ResolutionStatus.Should().Be(FormatResolutionStatus.Unavailable);
        recommendation.ProposedRedundantRecords.Should().BeEmpty();
    }

    [Fact]
    public void RecordsWithoutFormatsRequireManualReview()
    {
        ConsolidationRecommendation recommendation = Generate([
            Book(1, [], new(languages: ["eng"])),
            Book(2, [], new(languages: ["eng"])),
        ]);

        recommendation.Confidence.Should().Be(RecommendationConfidence.ManualReviewRequired);
        recommendation.Warnings.Should().Contain(value => value.Code == "FORMAT.NO_DECLARED_FORMATS");
    }

    [Fact]
    public void OverlappingStrongIdentifierSetsDoNotConflict()
    {
        CalibreBook first = BookWithIdentifiers(1, [new("isbn", "9780306406157")]);
        CalibreBook second = BookWithIdentifiers(2, [
            new("isbn", "9780306406157"),
            new("isbn13", "9783161484100"),
        ]);

        ConsolidationRecommendation recommendation = Generate([first, second]);

        recommendation.Warnings.Should().NotContain(value => value.Code == "IDENTIFIER.STRONG_CONFLICT");
        recommendation.RetainedSeparateRecords.Should().BeEmpty();
    }

    [Fact]
    public void MissingSeriesIndexIsIncompleteRatherThanConflicting()
    {
        CalibreBook first = Book(1, [], new(series: "Series", seriesIndex: 1, languages: ["eng"]));
        CalibreBook second = Book(2, [], new(series: "Series", languages: ["eng"]));

        ConsolidationRecommendation recommendation = Generate([first, second]);

        recommendation.Warnings.Should().NotContain(value => value.Code == "METADATA.SERIES_CONFLICT");
        recommendation.RetainedSeparateRecords.Should().BeEmpty();
    }

    [Fact]
    public void AssessmentObservedFingerprintChangesCanonicalInputIdentity()
    {
        CalibreBook first = Book(1, [Format("EPUB", 10, "01")], new(languages: ["eng"]));
        CalibreBook second = Book(2, [Format("EPUB", 20, "02")], new(languages: ["eng"]));
        FormatAssessment current = Assessment(first, 80, 80);
        FormatAssessment competitor = Assessment(second, 60, 60);
        FormatAssessment stale = new(
            current.CalibreBookId,
            current.Format,
            current.ExpectedRelativePath,
            Fingerprint(999, "ff"),
            current.Status,
            current.Score,
            current.AnalyzerVersion,
            current.ScoringModelVersion,
            current.Features,
            current.Findings);

        ConsolidationRecommendation currentRecommendation = Generate([first, second], [current, competitor]);
        ConsolidationRecommendation staleRecommendation = Generate([first, second], [stale, competitor]);

        staleRecommendation.InputVersion.Should().NotBe(currentRecommendation.InputVersion);
        staleRecommendation.FormatSelections.Single().ResolutionStatus.Should().Be(FormatResolutionStatus.UnresolvedConflict);
    }

    [Fact]
    public void FindingEnumerationOrderDoesNotChangeCanonicalInputIdentity()
    {
        CalibreBook[] books = [
            Book(1, [], new(languages: ["eng"])),
            Book(2, [], new(languages: ["eng"])),
        ];
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        LibraryFinding[] findings = [
            new("FORMAT_FILE_MISSING", FindingSeverity.Warning, "Missing PDF.", "Review it.", books[0].Id, "PDF", "b.pdf"),
            new("FORMAT_FILE_MISSING", FindingSeverity.Warning, "Missing EPUB.", "Review it.", books[0].Id, "EPUB", "a.epub"),
        ];
        ConsolidationRecommendationPolicy policy = new();

        ConsolidationRecommendation first = policy.Generate(Identity, group, books, [], [], findings, CancellationToken.None);
        ConsolidationRecommendation second = policy.Generate(Identity, group, books, [], [], findings.Reverse().ToArray(), CancellationToken.None);

        second.InputVersion.Should().Be(first.InputVersion);
    }

    private static ConsolidationRecommendation Generate(
        CalibreBook[] books,
        IReadOnlyList<FormatAssessment>? assessments = null)
    {
        ExactMetadataDuplicateGroup group = ExactMetadataDuplicateDetector.Detect(books).Single();
        return new ConsolidationRecommendationPolicy().Generate(
            Identity,
            group,
            books,
            ExactBinaryDuplicateDetector.Detect(books),
            assessments ?? [],
            [],
            CancellationToken.None);
    }

    private static CalibreBook Book(long id, BookFormat[] formats, BookPublicationMetadata? metadata = null) => new(
        new(id),
        "Shared Book",
        "Author, Shared",
        [new(new(id), "Shared Author", "Author, Shared")],
        [],
        formats,
        $"Author/Book ({id})",
        metadata);

    private static BookFormat Format(string format, long size, string digestSeed) => Format(format, Fingerprint(size, digestSeed), $"book-{digestSeed}.{format.ToLowerInvariant()}");

    private static BookFormat Format(string format, FormatFileFingerprint fingerprint, string path) => new(
        format,
        Path.GetFileNameWithoutExtension(path),
        path,
        FormatFileStatus.Present,
        fingerprint);

    private static FormatFileFingerprint Fingerprint(long size, string seed) => new(size, new Sha256Digest(seed.PadRight(64, seed[0])));

    private static CalibreBook BookWithIdentifiers(long id, BookIdentifier[] identifiers) => new(
        new(id),
        "Shared Book",
        "Author, Shared",
        [new(new(id), "Shared Author", "Author, Shared")],
        identifiers,
        [],
        $"Author/Book ({id})");

    private static FormatAssessment Assessment(CalibreBook book, int score, int decisiveAdjustment, string ruleId = "EPUB.NAVIGATION")
    {
        BookFormat format = book.Formats.Single();
        return new(
            book.Id,
            "EPUB",
            format.ExpectedRelativePath,
            format.Fingerprint,
            AssessmentStatus.Completed,
            new QualityScore(score),
            new("epub-inspector/1.0.1"),
            new("epub-quality/1.0.0"),
            new(true, true),
            [new AssessmentFinding(ruleId, FindingSeverity.Positive, decisiveAdjustment, "Synthetic assessment evidence.")]);
    }
}
