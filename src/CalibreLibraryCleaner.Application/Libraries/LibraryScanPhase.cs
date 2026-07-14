namespace CalibreLibraryCleaner.Application.Libraries;

public enum LibraryScanPhase
{
    Validating,
    OpeningDatabase,
    ValidatingSchema,
    ReadingBooks,
    ReadingAuthors,
    ReadingIdentifiers,
    ReadingFormats,
    ResolvingFiles,
    HashingFormats,
    GroupingExactDuplicates,
    Completed,
}
