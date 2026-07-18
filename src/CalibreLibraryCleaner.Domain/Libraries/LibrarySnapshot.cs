using System.Collections.ObjectModel;
using CalibreLibraryCleaner.Domain.Assessments;
using CalibreLibraryCleaner.Domain.Duplicates;
using CalibreLibraryCleaner.Domain.Findings;

namespace CalibreLibraryCleaner.Domain.Libraries;

public sealed record LibrarySnapshot
{
    public LibrarySnapshot(
        LibraryIdentity identity,
        DateTimeOffset scannedAt,
        IEnumerable<CalibreBook> books,
        IEnumerable<LibraryFinding> findings,
        IEnumerable<ExactBinaryDuplicateGroup>? exactBinaryDuplicateGroups = null,
        IEnumerable<ExactMetadataDuplicateGroup>? exactMetadataDuplicateGroups = null,
        IEnumerable<FormatAssessment>? epubAssessments = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(books);
        ArgumentNullException.ThrowIfNull(findings);

        Identity = identity;
        ScannedAt = scannedAt;
        Books = new ReadOnlyCollection<CalibreBook>(books.ToArray());
        Findings = new ReadOnlyCollection<LibraryFinding>(findings.ToArray());
        ExactBinaryDuplicateGroups = new ReadOnlyCollection<ExactBinaryDuplicateGroup>(
            (exactBinaryDuplicateGroups ?? []).ToArray());
        ExactMetadataDuplicateGroups = new ReadOnlyCollection<ExactMetadataDuplicateGroup>(
            (exactMetadataDuplicateGroups ?? []).ToArray());
        FormatAssessment[] orderedAssessments = (epubAssessments ?? [])
            .OrderBy(assessment => assessment.CalibreBookId.Value)
            .ThenBy(assessment => assessment.Format, StringComparer.Ordinal)
            .ThenBy(assessment => assessment.ExpectedRelativePath, StringComparer.Ordinal)
            .ToArray();
        if (orderedAssessments.Select(assessment => (assessment.CalibreBookId, assessment.Format, assessment.ExpectedRelativePath))
            .Distinct().Count() != orderedAssessments.Length)
        {
            throw new ArgumentException("EPUB assessment associations must be unique.", nameof(epubAssessments));
        }

        EpubAssessments = new ReadOnlyCollection<FormatAssessment>(orderedAssessments);
    }

    public LibraryIdentity Identity { get; }

    public DateTimeOffset ScannedAt { get; }

    public IReadOnlyList<CalibreBook> Books { get; }

    public IReadOnlyList<LibraryFinding> Findings { get; }

    public IReadOnlyList<ExactBinaryDuplicateGroup> ExactBinaryDuplicateGroups { get; }

    public IReadOnlyList<ExactMetadataDuplicateGroup> ExactMetadataDuplicateGroups { get; }

    public IReadOnlyList<FormatAssessment> EpubAssessments { get; }
}
