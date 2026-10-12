using CalibreLibraryCleaner.Application.Metadata;
using CalibreLibraryCleaner.Domain.Libraries;
using CalibreLibraryCleaner.Domain.Metadata;
using CalibreLibraryCleaner.Wpf.ViewModels;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Wpf.Tests.ViewModels;

public sealed class FinalAuthorReviewSubjectRowViewModelTests
{
    [Fact]
    public async Task RowDisplaysEvidenceAndPersistsApply()
    {
        List<(CalibreBookId Id, bool Apply)> persisted = [];
        FinalAuthorReviewSubjectRowViewModel row = new(
            Subject(AuthorProposalSource.ContentDerivedFrontMatter, apply: false),
            (id, apply) =>
            {
                persisted.Add((id, apply));
                return Task.CompletedTask;
            });

        row.CurrentAuthors.Should().Be("Unknown");
        row.ProposedAuthors.Should().Be("Reliable Writer");
        row.Roles.Should().Contain("Author: Reliable Writer").And.Contain("EPUB 1:2");
        row.NeedsReview.Should().BeTrue();
        row.Apply = true;
        await row.PersistApplyCommand.ExecuteAsync(null);

        persisted.Should().Equal((new CalibreBookId(1), true));
        row.MatchesFilter(FinalAuthorReviewFilterMode.Checked).Should().BeTrue();
    }

    [Fact]
    public void UnresolvedRowsCannotApply()
    {
        FinalAuthorReviewSubjectRowViewModel row = new(
            Subject(AuthorProposalSource.None, apply: false),
            (_, _) => Task.CompletedTask);

        row.CanApply.Should().BeFalse();
        row.PersistApplyCommand.CanExecute(null).Should().BeFalse();
        row.MatchesFilter(FinalAuthorReviewFilterMode.Unresolved).Should().BeTrue();
    }

    private static FinalAuthorReviewSubject Subject(
        AuthorProposalSource source,
        bool apply)
    {
        bool proposal = source != AuthorProposalSource.None;
        AuthorResolution resolution = new(
            proposal ? AuthorResolutionStatus.Suspect : AuthorResolutionStatus.Unresolved,
            CurrentAuthorProtection.Unverified,
            [AuthorSuspicionReasonCodes.Placeholder],
            proposal ? [new(AuthorEvidenceSource.EpubFrontMatter, "fixture", ["Reliable Writer"])] : [],
            source,
            proposal ? ["Reliable Writer"] : [],
            selectedByDefault: false);
        FrontMatterAuthorObservation evidence = new(
            "EPUB",
            new(10, new(new string('a', 64))),
            FrontMatterAuthorVersions.EpubExtractor,
            FrontMatterAuthorVersions.Limits,
            proposal ? FrontMatterAuthorStatus.Available : FrontMatterAuthorStatus.Missing,
            proposal ? [new(AuthorCreditRole.Author, ["Reliable Writer"], 1, 2, "By")] : [],
            3,
            3,
            100,
            false);
        return new(
            new(1),
            "The Book",
            [new(new(1), "Unknown", "Unknown")],
            [],
            resolution,
            proposal ? FinalAuthorEvidenceCoverage.EpubFrontMatter : FinalAuthorEvidenceCoverage.None,
            needsInspection: false,
            [evidence],
            apply);
    }
}
