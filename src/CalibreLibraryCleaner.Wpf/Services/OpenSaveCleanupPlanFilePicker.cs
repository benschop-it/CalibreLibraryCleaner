using Microsoft.Win32;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class OpenSaveCleanupPlanFilePicker : ICleanupPlanFilePicker
{
    private const string Filter = "Cleanup plan JSON (*.cleanup-plan.json)|*.cleanup-plan.json";

    public string? PickImportSource()
    {
        OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            Filter = Filter,
            Multiselect = false,
            Title = "Import cleanup plan",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickExportDestination()
    {
        SaveFileDialog dialog = new()
        {
            AddExtension = true,
            DefaultExt = ".cleanup-plan.json",
            FileName = "calibre-cleanup.cleanup-plan.json",
            Filter = Filter,
            OverwritePrompt = true,
            Title = "Export cleanup plan",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
