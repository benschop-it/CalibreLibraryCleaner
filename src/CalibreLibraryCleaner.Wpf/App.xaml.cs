using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Application.Recommendations;
using CalibreLibraryCleaner.Domain.Recommendations;
using CalibreLibraryCleaner.Infrastructure.DependencyInjection;
using CalibreLibraryCleaner.Wpf.Services;
using CalibreLibraryCleaner.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

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

        Log.Logger = ApplicationLogging.CreateLogger();
        Log.Information(
            "Application logging initialized. LogDirectory={LogDirectory}, RetainedFiles={RetainedFiles}, FileSizeLimitBytes={FileSizeLimitBytes}, FlushIntervalSeconds={FlushIntervalSeconds}.",
            ApplicationLogging.DefaultLogDirectory,
            ApplicationLogging.RetainedFileCountLimit,
            ApplicationLogging.FileSizeLimitBytes,
            ApplicationLogging.FlushInterval.TotalSeconds);
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(Log.Logger, dispose: false);
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
        builder.Services.AddSingleton<ResolveCandidateContentSignaturesUseCase>();
        builder.Services.AddSingleton<ResolveBibliographicEvidenceUseCase>();
        builder.Services.AddSingleton<PrepareResidualAnalysisFactsUseCase>();
        builder.Services.AddSingleton<IResidualAnalysisFactsPreparer>(serviceProvider =>
            serviceProvider.GetRequiredService<PrepareResidualAnalysisFactsUseCase>());
        builder.Services.AddSingleton<DiscoverWorkLanguageCandidatesUseCase>();
        builder.Services.AddSingleton<IWorkLanguageCandidateDiscoverer>(serviceProvider =>
            serviceProvider.GetRequiredService<DiscoverWorkLanguageCandidatesUseCase>());
        builder.Services.AddSingleton<AnalyzeResidualCandidatesUseCase>();
        builder.Services.AddSingleton<CandidatePreparationWorkflow>();
        builder.Services.AddSingleton<ICandidatePreparationWorkflow>(serviceProvider =>
            serviceProvider.GetRequiredService<CandidatePreparationWorkflow>());
        builder.Services.AddSingleton<ScanLibraryUseCase>();
        builder.Services.AddSingleton<RefreshAfterExactCleanupUseCase>();
        builder.Services.AddSingleton<ILibraryStateSession, LibraryStateSession>();
        builder.Services.AddSingleton(LibraryWorkflowOptions.Staged);
        builder.Services.AddSingleton<PersistedLibrarySnapshotsUseCase>();
        builder.Services.AddSingleton<ExportRecommendationsUseCase>();
        builder.Services.AddSingleton<ExecuteBulkExactDuplicateCleanupUseCase>();
        builder.Services.AddSingleton<ExecuteUnifiedCandidateCleanupUseCase>();
        builder.Services.AddSingleton<IUnifiedCandidateCleanupExecutor>(serviceProvider =>
            serviceProvider.GetRequiredService<ExecuteUnifiedCandidateCleanupUseCase>());
        builder.Services.AddSingleton(new LibraryAnalysisOptions());
        builder.Services.AddSingleton<ILibraryFolderPicker, OpenFolderDialogLibraryFolderPicker>();
        builder.Services.AddSingleton<IRecommendationExportFilePicker, SaveFileDialogRecommendationExportFilePicker>();
        builder.Services.AddSingleton<IExactDuplicateCleanupConfirmationService,
            MessageBoxExactDuplicateCleanupConfirmationService>();
        builder.Services.AddSingleton<IUnifiedCandidateCleanupConfirmationService,
            MessageBoxUnifiedCandidateCleanupConfirmationService>();
        builder.Services.AddSingleton<ExactBinaryCleanupPlanWorkspaceViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        _host = builder.Build();
    }

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);
            await _host.StartAsync().ConfigureAwait(true);
            await _host.Services.GetRequiredService<MainWindowViewModel>()
                .InitializeAsync(CancellationToken.None)
                .ConfigureAwait(true);
            _host.Services.GetRequiredService<MainWindow>().Show();
            Log.Information("Application startup completed.");
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "Application startup failed.");
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            throw;
        }
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        try
        {
            Log.Information("Application shutdown started.");
            await _host.StopAsync().ConfigureAwait(true);
            _host.Dispose();
            base.OnExit(e);
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(true);
        }
    }
}
