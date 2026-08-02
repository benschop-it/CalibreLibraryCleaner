namespace CalibreLibraryCleaner.Domain.Libraries;

public enum FormatFileStatus
{
    Present,
    ProjectedPresent,
    Missing,
    InvalidPath,
    Inaccessible,
    ChangedDuringHashing,
}
