using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class QuarantineReviewSubjectRowViewModelTests
{
    [Fact]
    public async Task RowDisplaysProvenanceAndPersistsInclude()
    {
        List<(CalibreBookId Id, bool Include)> persisted = [];
        FinalRecordDispositionLabel label = new(
            FinalRecordDispositionLabelSource.ManagedLayout,
            "Unknown");
        QuarantineReviewSubject subject = new(
            new(7),
            "The Book",
            [new(new(3), "Reliable Author", "Author, Reliable")],
            [new("EPUB", "Author/Book (7)/book.epub", FormatFileStatus.Present, null)],
            new([FinalRecordDispositionReasonCodes.PlaceholderLabel], [label]));
        QuarantineReviewSubjectRowViewModel row = new(subject, (id, include) =>
        {
            persisted.Add((id, include));
            return Task.CompletedTask;
        });

        row.Provenance.Should().Be("ManagedLayout: Unknown");
        row.Reasons.Should().Be(FinalRecordDispositionReasonCodes.PlaceholderLabel);
        row.Include = false;
        await row.PersistIncludeCommand.ExecuteAsync(null);

        persisted.Should().Equal((new CalibreBookId(7), false));
        row.MatchesFilter(QuarantineReviewFilterMode.Checked).Should().BeFalse();
        row.MatchesFilter(QuarantineReviewFilterMode.Proposed).Should().BeTrue();
    }

    [Fact]
    public void ManualRowsRemainDistinctFromPolicyProposals()
    {
        QuarantineReviewSubject subject = new(
            new(8),
            "Ordinary Book",
            [new(new(4), "Reliable Author", "Author, Reliable")],
            [],
            new([], []),
            manuallyIncluded: true);
        QuarantineReviewSubjectRowViewModel row = new(subject, (_, _) => Task.CompletedTask);

        row.IsManual.Should().BeTrue();
        row.IsProposed.Should().BeFalse();
        row.MatchesFilter(QuarantineReviewFilterMode.Manual).Should().BeTrue();
    }
}
