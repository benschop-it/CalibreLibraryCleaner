using Microsoft.Win32;

namespace CalibreLibraryCleaner.Wpf.Services;

internal sealed class SaveFileDialogRecommendationExportFilePicker : IRecommendationExportFilePicker
{
    public string? PickJsonDestination()
    {
        SaveFileDialog dialog = new()
        {
            AddExtension = true,
            DefaultExt = ".json",
            Filter = "Recommendation review JSON (*.json)|*.json",
            OverwritePrompt = true,
            Title = "Export recommendation review artifact",
            FileName = "calibre-recommendation-review.json",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
