namespace CalibreLibraryCleaner.Application.Libraries;

public enum LibraryErrorCode
{
    EmptyPath,
    FolderNotFound,
    FolderNotReadable,
    MetadataDatabaseNotFound,
    MetadataDatabaseNotAFile,
    MetadataDatabaseNotReadable,
    NotSqliteDatabase,
    UnsupportedSchema,
    CorruptDatabase,
    DatabaseBusy,
    HashingFailed,
    EpubAssessmentFailed,
    RecommendationGenerationFailed,
    RecommendationExportFailed,
    UnexpectedReadFailure,
}
