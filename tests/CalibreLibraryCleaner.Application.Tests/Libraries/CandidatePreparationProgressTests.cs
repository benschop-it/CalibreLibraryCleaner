using CalibreLibraryCleaner.Application.Libraries;
using FluentAssertions;
using Xunit;

namespace CalibreLibraryCleaner.Application.Tests.Libraries;

public sealed class CandidatePreparationProgressTests
{
    [Fact]
    public void KnownPhaseProgressRetainsTruthfulUnitsAndDetail()
    {
        CandidatePreparationProgress progress = new(
            CandidatePreparationPhase.AssessingEpubFormats,
            25,
            100,
            CandidateProgressUnit.Files,
            "Assessing EPUB files",
            "Content: 4 active",
            4);

        progress.Completed.Should().Be(25);
        progress.Total.Should().Be(100);
        progress.Unit.Should().Be(CandidateProgressUnit.Files);
        progress.Detail.Should().Be("Content: 4 active");
        progress.ActiveItems.Should().Be(4);
    }

    [Fact]
    public void InvalidCountersAreRejected()
    {
        FluentActions.Invoking(() => new CandidatePreparationProgress(
                CandidatePreparationPhase.AssessingPdfFormats,
                2,
                1,
                CandidateProgressUnit.Files,
                "Assessing PDF files"))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void OversizedDetailIsRejected()
    {
        FluentActions.Invoking(() => new CandidatePreparationProgress(
                CandidatePreparationPhase.AssessingEpubFormats,
                1,
                2,
                CandidateProgressUnit.Files,
                "Assessing EPUB files",
                new string('x', CandidatePreparationProgress.MaximumTextLength + 1)))
            .Should().Throw<ArgumentException>();
    }
}
