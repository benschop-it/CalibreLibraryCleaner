using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CalibreLibraryCleaner.Wpf;

public partial class App : System.Windows.Application
{
    private readonly IHost _host;

    public App()
    {
        // Enable legacy single-byte code pages (e.g. windows-1252) so documents that
        // declare a non-Unicode encoding decode accurately. Registration is process
        // global and idempotent; the infrastructure layer registers it too.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddCalibreLibraryInfrastructure();
        builder.Services.AddSingleton<ValidateLibraryUseCase>();
        builder.Services.AddSingleton<EpubAssessmentEngine>();
        builder.Services.AddSingleton<AssessEpubFormatsUseCase>();
        builder.Services.AddSingleton<PdfPageSamplingPolicy>();
        builder.Services.AddSingleton<PdfClassificationPolicy>();
        builder.Services.AddSingleton<PdfAssessmentEngine>();
        builder.Services.AddSingleton<AssessPdfFormatsUseCase>();
        builder.Services.AddSingleton<ConsolidationRecommendationPolicy>();
        builder.Services.AddSingleton<GenerateConsolidationRecommendationsUseCase>();
        builder.Services.AddSingleton<ScanLibraryUseCase>();
        builder.Services.AddSingleton<ILibraryStateSession, LibraryStateSession>();
        builder.Services.AddSingleton<PersistedLibrarySnapshotsUseCase>();
        builder.Services.AddSingleton<ExportRecommendationsUseCase>();
        builder.Services.AddSingleton<ExecuteBulkExactDuplicateCleanupUseCase>();
        builder.Services.AddSingleton<ExecuteBulkMetadataCandidateCleanupUseCase>();
        builder.Services.AddSingleton(new LibraryAnalysisOptions());
        builder.Services.AddSingleton<ILibraryFolderPicker, OpenFolderDialogLibraryFolderPicker>();
        builder.Services.AddSingleton<IRecommendationExportFilePicker, SaveFileDialogRecommendationExportFilePicker>();
        builder.Services.AddSingleton<IExactDuplicateCleanupConfirmationService,
            MessageBoxExactDuplicateCleanupConfirmationService>();
        builder.Services.AddSingleton<IMetadataCandidateCleanupConfirmationService,
            MessageBoxMetadataCandidateCleanupConfirmationService>();
        builder.Services.AddSingleton<ExactBinaryCleanupPlanWorkspaceViewModel>();
        builder.Services.AddSingleton<MetadataCandidateCleanupWorkspaceViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        _host = builder.Build();
    }

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        await _host.StartAsync().ConfigureAwait(true);
        await _host.Services.GetRequiredService<MainWindowViewModel>()
            .InitializeAsync(CancellationToken.None)
            .ConfigureAwait(true);
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        await _host.StopAsync().ConfigureAwait(true);
        _host.Dispose();
        base.OnExit(e);
    }
}
