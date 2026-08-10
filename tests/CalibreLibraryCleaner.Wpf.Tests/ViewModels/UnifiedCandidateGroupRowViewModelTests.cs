using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Matching;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class UnifiedCandidateGroupRowViewModelTests
{
    [Fact]
    public void KeeperOverrideUpdatesTitleAuthorsIdAndMemberActions()
    {
        CalibreBook first = Book(1, "First title", "First Author");
        CalibreBook second = Book(2, "Second title", "Second Author");
        WorkLanguageCandidateGroup expanded = WorkLanguageCandidateGroup.Create(
            "en",
            [first.Id, second.Id],
            [first.Id],
            WorkLanguageCandidateConfidence.Strong,
            [new("MATCH.CONTENT.EQUIVALENT", CandidateEvidenceStrength.Anchor)],
            contentComparison: new(1, 1, 0, 0, 0, 0));
        UnifiedCandidateGroup group = UnifiedCandidateMergePolicy.Merge(
            [], [expanded], [first, second]).Single();
        UnifiedCandidateGroupRowViewModel row = new(
            group, new Dictionary<CalibreBookId, CalibreBook> { [first.Id] = first, [second.Id] = second });
        List<string?> changed = [];
        row.PropertyChanged += (_, eventArgs) => changed.Add(eventArgs.PropertyName);
        UnifiedCandidateMemberRowViewModel alternate = row.Members.Single(value => value.BookId == 2);

        row.KeeperMember = alternate;

        row.KeeperRecordId.Should().Be(2);
        row.KeeperTitle.Should().Be("Second title");
        row.KeeperAuthors.Should().Be("Second Author");
        row.KeeperWasOverridden.Should().BeTrue();
        row.Members.Single(value => value.BookId == 1).Action.Should().Be("Remove");
        alternate.Action.Should().Be("Keep");
        changed.Should().Contain([
            nameof(UnifiedCandidateGroupRowViewModel.KeeperMember),
            nameof(UnifiedCandidateGroupRowViewModel.KeeperRecordId),
            nameof(UnifiedCandidateGroupRowViewModel.KeeperTitle),
            nameof(UnifiedCandidateGroupRowViewModel.KeeperAuthors),
        ]);
        row.Skip.Should().BeFalse();
    }

    private static CalibreBook Book(long id, string title, string author)
    {
        FormatFileFingerprint fingerprint = new(100 + id, new(new string('a', 64)));
        return new(
            new(id),
            title,
            author,
            [new(new(id), author, author)],
            [],
            [new(
                "EPUB",
                title,
                $"Author/{title}.epub",
                FormatFileStatus.Present,
                fingerprint,
                new(fingerprint.SizeInBytes, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0))],
            "Author",
            new(languages: ["eng"]));
    }
}
