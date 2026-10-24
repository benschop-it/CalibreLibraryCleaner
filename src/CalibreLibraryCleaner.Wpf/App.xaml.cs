using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Assessments.Pdf;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Matching;
using CalibreLibraryCleaner.Application.Metadata;
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
    private readonly IHost? _host;
    private readonly bool _releaseSmoke;
    private readonly string? _startupStorageError;

    public App()
    {
        _releaseSmoke = Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(value => string.Equals(value, "--release-smoke", StringComparison.Ordinal));
        if (_releaseSmoke) return;

        // Enable legacy single-byte code pages (e.g. windows-1252) so documents that
        // declare a non-Unicode encoding decode accurately. Registration is process
        // global and idempotent; the infrastructure layer registers it too.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        ApplicationStorageRoot storage;
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        string logDirectory;
        try
        {
            storage = new(Environment.GetEnvironmentVariable(ApplicationStorageRoot.EnvironmentVariable));
            logDirectory = storage.GetPath("logs");
            builder.Services.AddCalibreLibraryInfrastructure(
                storage,
                onlineMetadataEnabled: !string.Equals(
                    Environment.GetEnvironmentVariable("CALIBRE_ONLINE_METADATA_ENABLED"), "0", StringComparison.Ordinal));
            Log.Logger = ApplicationLogging.CreateLogger(logDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                           or System.IO.IOException or UnauthorizedAccessException)
        {
            _startupStorageError = "The configured application storage is unavailable or unsafe. "
                + "Use an absolute physical directory outside every Calibre library.";
            return;
        }
        Log.Information(
            "Application logging initialized. LogDirectory={LogDirectory}, RetainedFiles={RetainedFiles}, FileSizeLimitBytes={FileSizeLimitBytes}, FlushIntervalSeconds={FlushIntervalSeconds}.",
            logDirectory,
            ApplicationLogging.RetainedFileCountLimit,
            ApplicationLogging.FileSizeLimitBytes,
            ApplicationLogging.FlushInterval.TotalSeconds);
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(Log.Logger, dispose: false);
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
        builder.Services.AddSingleton<ResolveEditionMetadataProposalsUseCase>();
        builder.Services.AddSingleton<PrepareMetadataReviewUseCase>();
        builder.Services.AddSingleton<InspectEmbeddedAuthorMetadataUseCase>();
        builder.Services.AddSingleton<InspectFrontMatterAuthorsUseCase>();
        builder.Services.AddSingleton<ObserveLocalEmbeddingEvidenceUseCase>(serviceProvider =>
        {
            Infrastructure.LocalModels.OllamaEmbeddingOptions options = serviceProvider
                .GetRequiredService<Infrastructure.LocalModels.OllamaEmbeddingOptions>();
            Domain.Matching.LocalEmbeddingModelIdentity? model = options.Enabled
                ? new(
                    "ollama",
                    options.RuntimeVersion!,
                    options.ModelId!,
                    options.ModelVersion!,
                    options.Dimensions)
                : null;
            return new(
                serviceProvider.GetRequiredService<ILocalEmbeddingProvider>(),
                serviceProvider.GetRequiredService<ILocalEmbeddingComparisonCache>(),
                model);
        });
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
        builder.Services.AddSingleton<ReconcileFinalAuthorCatalogUseCase>();
        builder.Services.AddSingleton<IFinalAuthorCatalogReconciler>(serviceProvider =>
            serviceProvider.GetRequiredService<ReconcileFinalAuthorCatalogUseCase>());
        builder.Services.AddSingleton<IUnifiedCandidateCleanupExecutor>(serviceProvider =>
            serviceProvider.GetRequiredService<ExecuteUnifiedCandidateCleanupUseCase>());
        builder.Services.AddSingleton<ExecuteQuarantineCleanupUseCase>();
        builder.Services.AddSingleton<IQuarantineCleanupExecutor>(serviceProvider =>
            serviceProvider.GetRequiredService<ExecuteQuarantineCleanupUseCase>());
        builder.Services.AddSingleton(new LibraryAnalysisOptions());
        builder.Services.AddSingleton<ILibraryFolderPicker, OpenFolderDialogLibraryFolderPicker>();
        builder.Services.AddSingleton<IRecommendationExportFilePicker, SaveFileDialogRecommendationExportFilePicker>();
        builder.Services.AddSingleton<IExactDuplicateCleanupConfirmationService,
            MessageBoxExactDuplicateCleanupConfirmationService>();
        builder.Services.AddSingleton<IUnifiedCandidateCleanupConfirmationService,
            MessageBoxUnifiedCandidateCleanupConfirmationService>();
        builder.Services.AddSingleton<IMetadataMutationConfirmationService,
            MessageBoxMetadataMutationConfirmationService>();
        builder.Services.AddSingleton<IQuarantineCleanupConfirmationService,
            MessageBoxQuarantineCleanupConfirmationService>();
        builder.Services.AddSingleton<LibraryOperationCoordinator>();
        builder.Services.AddTransient<OnlineMetadataSettingsViewModel>();
        builder.Services.AddTransient<OnlineMetadataSettingsWindow>();
        builder.Services.AddSingleton<IOnlineMetadataSettingsDialogService>(serviceProvider =>
            new OnlineMetadataSettingsDialogService(
                serviceProvider.GetRequiredService<OnlineMetadataSettingsWindow>));
        builder.Services.AddSingleton<ExactBinaryCleanupPlanWorkspaceViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        _host = builder.Build();
    }

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (_startupStorageError is not null)
        {
            base.OnStartup(e);
            System.Windows.MessageBox.Show(_startupStorageError, "Application storage",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        if (_releaseSmoke)
        {
            base.OnStartup(e);
            Shutdown(ReleasePackageSmokeCheck.Validate(AppContext.BaseDirectory) ? 0 : 1);
            return;
        }
        try
        {
            base.OnStartup(e);
            await _host!.StartAsync().ConfigureAwait(true);
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
        if (_host is null)
        {
            base.OnExit(e);
            return;
        }
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
