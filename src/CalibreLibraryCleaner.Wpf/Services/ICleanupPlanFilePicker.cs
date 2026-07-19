namespace CalibreLibraryCleaner.Wpf.Services;

public interface ICleanupPlanFilePicker
{
    string? PickImportSource();
    string? PickExportDestination();
}
