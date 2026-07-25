using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Plans;
using CalibreLibraryCleaner.Domain.Recoveries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Domain.Tests.Recoveries;

public sealed class RecoveryVerificationTests
{
    [Fact]
    public void SemanticRestorationAcceptsNewCalibreIdAndPersistsMapping()
    {
        FormatFileFingerprint fingerprint = new(4, new(new string('a', 64)));
        FormatFileObservation observation = new(4, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, 32);
        ExpectedRecordState expectedRecord = new(new(1), "Title", "Author",
            [new(new(1), "Author", "Author")], [new("isbn", "123")],
            null, null, null, null, ["eng"], false, "Author/Title (1)",
            [new(new(1), "EPUB", "book", "Author/Title (1)/book.epub",
                FormatFileStatus.Present, fingerprint, observation)]);
        CalibreBook recovered = new(new(42), "Title", "Author",
            [new(new(1), "Author", "Author")], [new("isbn", "123")],
            [new("EPUB", "book", "Author/Title (42)/book.epub",
                FormatFileStatus.Present, fingerprint, observation)],
            "Author/Title (42)", new(languages: ["eng"]));
        LogicalRecoveryRecordId logical = new("record-test");
        RecoveryRecordIdMapping mapping = new(logical, new(1), null, new(42),
            ["EPUB"], ["isbn:123"], DateTimeOffset.UtcNow);
        ExpectedRecoveredState expected = new(
            [new(logical, expectedRecord, null,
                ["title", "authors", "author_sort", "identifiers", "languages"])],
            [], [], [], new(new string('0', 64)));
        LibrarySnapshot snapshot = new(new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            27, "C:\\synthetic"), DateTimeOffset.UtcNow, [recovered], []);

        RecoveryVerificationResult result = RecoveryVerificationPolicy.VerifyFinalState(
            expected, snapshot, [mapping], DateTimeOffset.UtcNow);

        result.IsVerified.Should().BeTrue();
        result.RecordIdMappings.Should().ContainSingle()
            .Which.NumericIdChanged.Should().BeTrue();
    }

    [Fact]
    public void WrongProcessEffectAndLostUnexpectedContentFailVerification()
    {
        LogicalRecoveryRecordId logical = new("record-test");
        FormatFileFingerprint expectedFingerprint = new(4, new(new string('a', 64)));
        ExpectedRecoveredState expected = new([], [
            new(logical, new(7), "AZW3", expectedFingerprint, "preserve user content"),
        ], [], [], new(new string('0', 64)));
        LibrarySnapshot empty = new(new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            27, "C:\\synthetic"), DateTimeOffset.UtcNow, [], []);

        RecoveryVerificationResult result = RecoveryVerificationPolicy.VerifyFinalState(
            expected, empty, [], DateTimeOffset.UtcNow);

        result.IsVerified.Should().BeFalse();
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.PRESERVED_CONTENT_MISSING");
    }

    [Fact]
    public void UnplannedAffectedRecordFormatCannotBeReportedAsRecovered()
    {
        FormatFileFingerprint expectedFingerprint = new(4, new(new string('a', 64)));
        FormatFileFingerprint unexpectedFingerprint = new(5, new(new string('b', 64)));
        FormatFileObservation expectedObservation = new(4, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, 32);
        FormatFileObservation unexpectedObservation = new(5, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, 32);
        ExpectedRecordState expectedRecord = new(new(7), "Title", "Author",
            [new(new(1), "Author", "Author")], [],
            null, null, null, null, [], false, "Author/Title (7)",
            [new(new(7), "EPUB", "book", "Author/Title (7)/book.epub",
                FormatFileStatus.Present, expectedFingerprint, expectedObservation)]);
        CalibreBook actual = new(new(7), "Title", "Author",
            [new(new(1), "Author", "Author")], [],
            [
                new("EPUB", "book", "Author/Title (7)/book.epub",
                    FormatFileStatus.Present, expectedFingerprint, expectedObservation),
                new("PDF", "book", "Author/Title (7)/book.pdf",
                    FormatFileStatus.Present, unexpectedFingerprint, unexpectedObservation),
            ],
            "Author/Title (7)", new());
        LogicalRecoveryRecordId logical = new("record-test");
        ExpectedRecoveredState expected = new(
            [new(logical, expectedRecord, new(7), ["title"])],
            [], [], [], new(new string('0', 64)));
        LibrarySnapshot snapshot = new(new("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            27, "C:\\synthetic"), DateTimeOffset.UtcNow, [actual], []);

        RecoveryVerificationResult result = RecoveryVerificationPolicy.VerifyFinalState(
            expected, snapshot, [], DateTimeOffset.UtcNow);

        result.IsVerified.Should().BeFalse();
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.UNEXPECTED_AFFECTED_FORMAT"
            && value.CurrentRecordId == new CalibreBookId(7)
            && value.Format == "PDF");
    }
}
