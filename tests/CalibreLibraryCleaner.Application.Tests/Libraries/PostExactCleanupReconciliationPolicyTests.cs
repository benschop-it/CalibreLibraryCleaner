using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Domain.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class PostExactCleanupReconciliationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly LibraryStateGenerationId Generation = new(
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
    private static readonly FormatFileFingerprint Epub = Fingerprint('a', 10);
    private static readonly FormatFileFingerprint Pdf = Fingerprint('b', 20);

    [Fact]
    public void UnchangedAssociationsReusePreExactFingerprints()
    {
        LibraryState pre = PreState([Book(1, Format(1, "EPUB", Epub))]);
        LibraryState post = CompleteExact(pre, []);

        PostExactReconciliationResult result = PostExactCleanupReconciliationPolicy.Reconcile(
            pre, post, [], Catalog(BookRecord(1, ("EPUB", "book"))));

        result.IsSuccess.Should().BeTrue();
        result.Decisions.Should().ContainSingle().Which.Should().Be(new PostExactAssociationDecision(
            new(new(1), "EPUB"), PostExactAssociationDisposition.Unchanged, Epub));
    }

    [Fact]
    public void UnchangedInvalidPathAssociationIsPreservedAsNonExecutable()
    {
        BookFormat invalid = new("EPUB", "book", string.Empty, FormatFileStatus.InvalidPath);
        LibraryState pre = PreState([Book(1, invalid)]);
        LibraryState post = CompleteExact(pre, []);

        PostExactReconciliationResult result = PostExactCleanupReconciliationPolicy.Reconcile(
            pre, post, [], Catalog(BookRecord(1, ("EPUB", "book"))));

        result.IsSuccess.Should().BeTrue();
        result.Decisions.Should().ContainSingle().Which.Should().Be(new PostExactAssociationDecision(
            new(new(1), "EPUB"), PostExactAssociationDisposition.PreservedInvalidPath, null));
    }

    [Fact]
    public void TransferTargetRequiresHashWithCurrentWorkerEvidence()
    {
        LibraryState pre = PreState([
            Book(1, Format(1, "EPUB", Epub)),
            Book(2, Format(2, "PDF", Pdf)),
        ]);
        LibraryStateDelta[] deltas =
        [
            new AddOrReplaceFormatLibraryStateDelta(Generation, new(0), "add", Now.AddSeconds(1),
                new(1), "PDF", Pdf, null),
            new RemoveFormatLibraryStateDelta(Generation, new(1), "remove-pdf", Now.AddSeconds(1),
                new(2), "PDF", Pdf),
            new RemoveRecordLibraryStateDelta(Generation, new(2), "remove-record", Now.AddSeconds(1), new(2)),
        ];
        LibraryState post = CompleteExact(pre, deltas);

        PostExactReconciliationResult result = PostExactCleanupReconciliationPolicy.Reconcile(
            pre, post, deltas, Catalog(BookRecord(1, ("EPUB", "book"), ("PDF", "book"))));

        result.IsSuccess.Should().BeTrue();
        result.Decisions.Should().Contain(value => value.Association == new PostExactAssociationKey(new(1), "PDF")
            && value.Disposition == PostExactAssociationDisposition.TargetedHashRequired
            && value.ExpectedFingerprint == Pdf
            && value.IsTransferTarget);
        result.Decisions.Should().Contain(value => value.Association == new PostExactAssociationKey(new(2), "PDF")
            && value.Disposition == PostExactAssociationDisposition.ExpectedRemoved);
    }

    [Fact]
    public void ProvenTransferCanBeClassifiedWithoutTargetHash()
    {
        LibraryState pre = PreState([
            Book(1, Format(1, "EPUB", Epub)),
            Book(2, Format(2, "PDF", Pdf)),
        ]);
        LibraryStateDelta[] deltas =
        [
            new AddOrReplaceFormatLibraryStateDelta(Generation, new(0), "add", Now.AddSeconds(1),
                new(1), "PDF", Pdf, null),
            new RemoveFormatLibraryStateDelta(Generation, new(1), "remove", Now.AddSeconds(1),
                new(2), "PDF", Pdf),
            new RemoveRecordLibraryStateDelta(Generation, new(2), "remove-record", Now.AddSeconds(1), new(2)),
        ];
        LibraryState post = CompleteExact(pre, deltas);

        PostExactReconciliationResult result = PostExactCleanupReconciliationPolicy.Reconcile(
            pre, post, deltas, Catalog(BookRecord(1, ("EPUB", "book"), ("PDF", "book"))),
            new(RequireTransferTargetHash: false));

        result.IsSuccess.Should().BeTrue();
        result.Decisions.Should().Contain(value => value.Disposition == PostExactAssociationDisposition.Transferred);
    }

    [Theory]
    [InlineData("record")]
    [InlineData("metadata")]
    [InlineData("format")]
    [InlineData("stored-name")]
    [InlineData("missing-format")]
    public void UnexplainedCatalogChangesFailClosed(string change)
    {
        LibraryState pre = PreState([Book(1, Format(1, "EPUB", Epub))]);
        LibraryState post = CompleteExact(pre, []);
        CalibreBookRecord first = BookRecord(1, ("EPUB", change == "stored-name" ? "changed" : "book"));
        if (change == "metadata") first = first with { Title = "Changed" };
        if (change == "format") first = first with
        {
            Formats = [.. first.Formats, new("PDF", "book")],
        };
        if (change == "missing-format") first = first with { Formats = [] };
        CalibreCatalogRecord catalog = change == "record"
            ? Catalog(first, BookRecord(2, ("EPUB", "book")))
            : Catalog(first);

        PostExactReconciliationResult result = PostExactCleanupReconciliationPolicy.Reconcile(
            pre, post, [], catalog);

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().NotBeEmpty();
    }

    [Fact]
    public void MissingExpectedRecordFailsClosed()
    {
        LibraryState pre = PreState([Book(1, Format(1, "EPUB", Epub))]);
        LibraryState post = CompleteExact(pre, []);

        PostExactReconciliationResult result = PostExactCleanupReconciliationPolicy.Reconcile(
            pre, post, [], Catalog());

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "POST_EXACT.MISSING_RECORD");
    }

    [Fact]
    public void NonExactDeltaKindFailsClosed()
    {
        LibraryState pre = PreState([Book(1, Format(1, "EPUB", Epub))]);
        LibraryStateDelta[] deltas =
        [
            new SetMetadataLibraryStateDelta(Generation, new(0), "metadata", Now.AddSeconds(1),
                new(1), LibraryMetadataField.Title, ["Changed"]),
        ];
        LibraryState projected = LibraryStateDeltaPolicy.ApplyBatch(pre, deltas)
            .AdvanceWorkflow(LibraryWorkflowPhase.CandidatePreparationReady, Now.AddSeconds(2));

        PostExactReconciliationResult result = PostExactCleanupReconciliationPolicy.Reconcile(
            pre, projected, deltas, Catalog(BookRecord(1, ("EPUB", "book"))));

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "POST_EXACT.DELTA_SEQUENCE_INVALID");
    }

    [Fact]
    public void TransferWithoutMatchingSourceRemovalFailsClosed()
    {
        LibraryState pre = PreState([Book(1, Format(1, "EPUB", Epub))]);
        LibraryStateDelta[] deltas =
        [
            new AddOrReplaceFormatLibraryStateDelta(Generation, new(0), "forged-add", Now.AddSeconds(1),
                new(1), "PDF", Pdf, null),
        ];
        LibraryState post = CompleteExact(pre, deltas);

        PostExactReconciliationResult result = PostExactCleanupReconciliationPolicy.Reconcile(
            pre, post, deltas, Catalog(BookRecord(1, ("EPUB", "book"), ("PDF", "book"))));

        result.IsSuccess.Should().BeFalse();
        result.Issues.Should().Contain(value => value.Code == "POST_EXACT.TRANSFER_SOURCE_UNPROVEN");
    }

    private static LibraryState PreState(IEnumerable<CalibreBook> books)
    {
        LibrarySnapshot snapshot = new(
            new("87f7ed1f-59a8-45a6-975a-7e06fd84780d", 27, "C:\\library"),
            Now,
            books,
            []);
        return LibraryState.FromScan(snapshot, Generation)
            .AdvanceWorkflow(LibraryWorkflowPhase.ExactReady, Now);
    }

    private static LibraryState CompleteExact(LibraryState pre, LibraryStateDelta[] deltas)
    {
        LibraryState projected = deltas.Length == 0 ? pre : LibraryStateDeltaPolicy.ApplyBatch(pre, deltas);
        return projected.AdvanceWorkflow(
            LibraryWorkflowPhase.CandidatePreparationReady,
            projected.ProjectedAtUtc.AddSeconds(1));
    }

    private static CalibreBook Book(long id, params BookFormat[] formats) => new(
        new(id),
        $"Book {id}",
        "Author",
        [new(new(id), "Author", "Author")],
        [new("isbn", $"978000000000{id}")],
        formats,
        $"Author/Book {id} ({id})",
        new("Publisher", Now.AddYears(-1), "Series", 1, ["eng"], true));

    private static BookFormat Format(long id, string format, FormatFileFingerprint fingerprint) => new(
        format,
        "book",
        $"Author/Book {id} ({id})/book.{format.ToLowerInvariant()}",
        FormatFileStatus.Present,
        fingerprint,
        new(fingerprint.SizeInBytes, Now, Now, 0));

    private static CalibreBookRecord BookRecord(long id, params (string Format, string StoredName)[] formats) => new(
        id,
        $"Book {id}",
        "Author",
        $"Author/Book {id} ({id})",
        [new(id, "Author", "Author")],
        [new("isbn", $"978000000000{id}")],
        formats.Select(value => new CalibreFormatRecord(value.Format, value.StoredName)).ToArray(),
        new("Publisher", Now.AddYears(-1), "Series", 1, ["eng"], true));

    private static CalibreCatalogRecord Catalog(params CalibreBookRecord[] books) => new(
        "87f7ed1f-59a8-45a6-975a-7e06fd84780d",
        27,
        books);

    private static FormatFileFingerprint Fingerprint(char value, long size) => new(
        size,
        new(new string(value, 64)));
}
