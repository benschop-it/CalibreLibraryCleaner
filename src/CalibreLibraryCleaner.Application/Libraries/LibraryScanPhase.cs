namespace CalibreLibraryCleaner.Application.Libraries;

public enum LibraryScanPhase
{
    Validating,
    OpeningDatabase,
    ValidatingSchema,
    ReadingBooks,
    ReadingAuthors,
    ReadingIdentifiers,
    ReadingPublicationMetadata,
    ReadingFormats,
    ResolvingFiles,
    HashingFormats,
    AssessingEpubFormats,
    GroupingExactDuplicates,
    GroupingExactMetadataDuplicates,
    GeneratingConsolidationRecommendations,
    Completed,
}
