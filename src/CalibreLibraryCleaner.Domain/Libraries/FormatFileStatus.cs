namespace CalibreLibraryCleaner.Domain.Libraries;

public enum FormatFileStatus
{
    Present,
    Missing,
    InvalidPath,
    Inaccessible,
    ChangedDuringHashing,
}
