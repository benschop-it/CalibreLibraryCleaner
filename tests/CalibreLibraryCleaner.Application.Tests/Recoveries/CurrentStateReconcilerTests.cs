using CalibreLibraryCleaner.Application.Recoveries;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Recoveries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Recoveries;

public sealed class CurrentStateReconcilerTests
{
    [Fact]
    public void CompletedCleanupIsReconciledFromJournalAndActualState()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current, _, _) =
            RecoveryTestData.SourceAndCurrent();

        CurrentStateReconciliation result = new CurrentStateReconciler().Reconcile(
            source, current);

        result.Records.Should().Contain(record =>
            record.Classifications.Contains(
                RecoveryReconciliationClassification.CompletedAndMatchesJournalPostState));
        result.Records.Should().Contain(record =>
            record.Classifications.Contains(RecoveryReconciliationClassification.Missing));
    }

    [Fact]
    public void UnexpectedUniqueFormatIsPreservedAndNeverSelectedByNameSizeOrTime()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current, _, _) =
            RecoveryTestData.SourceAndCurrent();
        CalibreBook target = current.Snapshot.Books.Single();
        FormatFileFingerprint unique = new(5, new(new string('7', 64)));
        BookFormat unexpected = new("AZW3", "book",
            $"{target.RelativeDirectory}/book.azw3", FormatFileStatus.Present,
            unique, new(5, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 32));
        CalibreBook changed = new(target.Id, target.Title, target.AuthorSort,
            target.Authors, target.Identifiers, target.Formats.Append(unexpected),
            target.RelativeDirectory, target.PublicationMetadata);
        LibrarySnapshot snapshot = new(current.Snapshot.Identity,
            current.Snapshot.ScannedAt.AddMinutes(1), [changed], []);
        RecoveryCurrentStateSnapshot changedCurrent = RecoveryTestData.CurrentFrom(source, snapshot);

        CurrentStateReconciliation result = new CurrentStateReconciler().Reconcile(
            source, changedCurrent);

        ReconciledRecoveryRecord record = result.Records.Single(value =>
            value.Identity.CurrentRecordId == target.Id);
        record.Classifications.Should().Contain(
            RecoveryReconciliationClassification.UnexpectedUniqueContent);
        record.Formats.Should().Contain(value =>
            value.Format == "AZW3" && value.PreserveCurrentContent
            && value.CurrentFingerprint == unique);
    }

    [Fact]
    public void ReconciliationDigestChangesWithSemanticCurrentContent()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current, _, _) =
            RecoveryTestData.SourceAndCurrent();
        CurrentStateReconciliation first = new CurrentStateReconciler().Reconcile(source, current);
        CalibreBook target = current.Snapshot.Books.Single();
        CalibreBook changed = new(target.Id, target.Title + " changed", target.AuthorSort,
            target.Authors, target.Identifiers, target.Formats,
            target.RelativeDirectory, target.PublicationMetadata);
        RecoveryCurrentStateSnapshot changedCurrent = RecoveryTestData.CurrentFrom(source,
            new(current.Snapshot.Identity, current.Snapshot.ScannedAt.AddMinutes(1), [changed], []));

        CurrentStateReconciliation second = new CurrentStateReconciler().Reconcile(
            source, changedCurrent);

        second.Digest.Should().NotBe(first.Digest);
        second.Records.Should().Contain(value => value.Classifications.Contains(
            RecoveryReconciliationClassification.IndependentlyModifiedAfterExecution));
    }

    [Fact]
    public void UnqualifiedCurrentCoverContentBlocksAutomaticRecovery()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current, _, _) =
            RecoveryTestData.SourceAndCurrent();
        CalibreBook target = current.Snapshot.Books.Single();
        BookPublicationMetadata metadata = target.PublicationMetadata;
        CalibreBook withCover = new(target.Id, target.Title, target.AuthorSort,
            target.Authors, target.Identifiers, target.Formats,
            target.RelativeDirectory, new(metadata.Publisher,
                metadata.PublicationDate, metadata.Series, metadata.SeriesIndex,
                metadata.Languages, hasCover: true));
        RecoveryCurrentStateSnapshot changed = RecoveryTestData.CurrentFrom(source,
            new(current.Snapshot.Identity, current.Snapshot.ScannedAt.AddMinutes(1),
                [withCover], []));

        CurrentStateReconciliation result = new CurrentStateReconciler().Reconcile(
            source, changed);

        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.COVER_STATE_UNSUPPORTED"
            && value.Severity == RecoveryIssueSeverity.Blocking);
    }

    [Fact]
    public void ReusedNumericIdWithoutSemanticEvidenceIsNeverTreatedAsTheOriginal()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current, _, _) =
            RecoveryTestData.SourceAndCurrent();
        CalibreBook target = current.Snapshot.Books.Single();
        CalibreBook unrelatedAtOriginalId = new(new(2), "Unrelated record",
            "Writer, Other", [new BookAuthor(new(900), "Other Writer", "Writer, Other")],
            [new BookIdentifier("custom", "unrelated-identity")], [],
            "Other/Unrelated (2)",
            new(null, null, null, null, [], hasCover: false));
        RecoveryCurrentStateSnapshot changed = RecoveryTestData.CurrentFrom(source,
            new(current.Snapshot.Identity, current.Snapshot.ScannedAt.AddMinutes(1),
                [target, unrelatedAtOriginalId], []));

        CurrentStateReconciliation result = new CurrentStateReconciler().Reconcile(
            source, changed);

        ReconciledRecoveryRecord record = result.Records.Single(value =>
            value.Identity.OriginalRecordId == new CalibreBookId(2));
        record.Identity.CurrentRecordId.Should().BeNull();
        record.Classifications.Should().Contain(
            RecoveryReconciliationClassification.Ambiguous);
        result.Issues.Should().Contain(value =>
            value.Code == "RECOVERY.NUMERIC_ID_REUSED"
            && value.Severity == RecoveryIssueSeverity.Blocking);
    }

    [Fact]
    public void StableIdentifierAloneNeverMatchesADifferentNumericRecord()
    {
        (RecoverySourceInspection source, RecoveryCurrentStateSnapshot current, _, _) =
            RecoveryTestData.SourceAndCurrent();
        CalibreBook target = current.Snapshot.Books.Single();
        CalibreBook sameIsbnOnly = new(new(42), "Different work",
            "Writer, Different",
            [new BookAuthor(new(901), "Different Writer", "Writer, Different")],
            [new BookIdentifier("isbn", "9780306406157")], [],
            "Different/Different work (42)",
            new(null, null, null, null, [], hasCover: false));
        RecoveryCurrentStateSnapshot changed = RecoveryTestData.CurrentFrom(source,
            new(current.Snapshot.Identity, current.Snapshot.ScannedAt.AddMinutes(1),
                [target, sameIsbnOnly], []));

        CurrentStateReconciliation result = new CurrentStateReconciler().Reconcile(
            source, changed);

        ReconciledRecoveryRecord record = result.Records.Single(value =>
            value.Identity.OriginalRecordId == new CalibreBookId(2));
        record.Identity.CurrentRecordId.Should().BeNull();
        record.Classifications.Should().Contain(
            RecoveryReconciliationClassification.Missing);
        record.AmbiguousCandidateIds.Should().BeEmpty();
    }
}
