using CalibreLibraryCleaner.Application.Libraries;
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
        builder.Services.AddSingleton<ScanLibraryUseCase>();
        builder.Services.AddSingleton(new LibraryAnalysisOptions());
        builder.Services.AddSingleton<ILibraryFolderPicker, OpenFolderDialogLibraryFolderPicker>();
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
