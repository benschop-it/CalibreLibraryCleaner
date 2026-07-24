using CalibreLibraryCleaner.Application.Abstractions;
using CalibreLibraryCleaner.Application.Assessments;
using CalibreLibraryCleaner.Application.Executions;
using CalibreLibraryCleaner.Application.Libraries;
using CalibreLibraryCleaner.Application.Plans;
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
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Services.AddCalibreLibraryInfrastructure();
        builder.Services.AddSingleton<ValidateLibraryUseCase>();
        builder.Services.AddSingleton<EpubAssessmentEngine>();
        builder.Services.AddSingleton<AssessEpubFormatsUseCase>();
        builder.Services.AddSingleton<ConsolidationRecommendationPolicy>();
        builder.Services.AddSingleton<GenerateConsolidationRecommendationsUseCase>();
        builder.Services.AddSingleton<ScanLibraryUseCase>();
        builder.Services.AddSingleton<IExecutionLibraryScanner, FullExecutionLibraryScanner>();
        builder.Services.AddSingleton<ExportRecommendationsUseCase>();
        builder.Services.AddSingleton<GenerateCleanupPlanUseCase>();
        builder.Services.AddSingleton<ValidateCleanupPlanUseCase>();
        builder.Services.AddSingleton<ApproveCleanupPlanUseCase>();
        builder.Services.AddSingleton<RevokeCleanupPlanUseCase>();
        builder.Services.AddSingleton<ExportCleanupPlanUseCase>();
        builder.Services.AddSingleton<ImportCleanupPlanUseCase>();
        builder.Services.AddSingleton<PrepareCleanupExecutionUseCase>();
        builder.Services.AddSingleton<ExecuteApprovedCleanupPlanUseCase>();
        builder.Services.AddSingleton<IPrepareCleanupExecution>(provider =>
            provider.GetRequiredService<PrepareCleanupExecutionUseCase>());
        builder.Services.AddSingleton<IExecuteApprovedCleanupPlan>(provider =>
            provider.GetRequiredService<ExecuteApprovedCleanupPlanUseCase>());
        builder.Services.AddSingleton(new LibraryAnalysisOptions());
        builder.Services.AddSingleton<ILibraryFolderPicker, OpenFolderDialogLibraryFolderPicker>();
        builder.Services.AddSingleton<IRecommendationExportFilePicker, SaveFileDialogRecommendationExportFilePicker>();
        builder.Services.AddSingleton<ICleanupPlanFilePicker, OpenSaveCleanupPlanFilePicker>();
        builder.Services.AddSingleton<ICleanupPlanConfirmationService, MessageBoxCleanupPlanConfirmationService>();
        builder.Services.AddSingleton<IExecutionBackupFolderPicker, OpenFolderDialogExecutionBackupFolderPicker>();
        builder.Services.AddSingleton<MessageBoxCleanupExecutionConfirmationService>();
        builder.Services.AddSingleton<ICleanupExecutionConfirmationService>(provider =>
            provider.GetRequiredService<MessageBoxCleanupExecutionConfirmationService>());
        builder.Services.AddSingleton<IDestructiveExecutionConfirmation>(provider =>
            provider.GetRequiredService<MessageBoxCleanupExecutionConfirmationService>());
        builder.Services.AddSingleton<CleanupPlanWorkspaceViewModel>();
        builder.Services.AddSingleton<CleanupExecutionWorkspaceViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        _host = builder.Build();
    }

    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        await _host.StartAsync().ConfigureAwait(true);
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override async void OnExit(System.Windows.ExitEventArgs e)
    {
        await _host.StopAsync().ConfigureAwait(true);
        _host.Dispose();
        base.OnExit(e);
    }
}
