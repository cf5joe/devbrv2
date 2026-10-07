using System.Windows;
using System.Windows.Threading;
using DevBR.App.Services;
using DevBR.App.Theming;
using DevBR.App.ViewModels;
using DevBR.App.Views;
using DevBR.Application.Archive;
using DevBR.Application.Settings;
using DevBR.Domain;
using DevBR.Infrastructure;
using DevBR.Infrastructure.Logging;
using DevBR.Infrastructure.Settings;
using DevBR.Infrastructure.State;
using DevBR.Infrastructure.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace DevBR.App;

/// <summary>
/// Composition root. Startup only prepares local state and the window: it performs no discovery, no
/// drive inspection and no network access, and the archive worker starts on first use.
/// </summary>
public partial class App
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new AppPaths();
        paths.EnsureCreated();
        Log.Logger = LogSetup.CreateLogger(paths, ProcessRole.Gui);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception.");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };

        try
        {
            _host = BuildHost(paths);
            await _host.StartAsync();

            await _host.Services.GetRequiredService<StateDatabase>().InitializeAsync(CancellationToken.None);
            _host.Services.GetRequiredService<ThemeService>().Initialize();

            var window = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();

            Log.Information("DevBR {Version} ({Channel}) started.", BuildInfo.Version, BuildInfo.ReleaseChannel);
            await _host.Services.GetRequiredService<ActivityStore>().AddAsync(EventSeverity.Information, "Application", $"DevBR {BuildInfo.Version} started.");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "DevBR failed to start.");
            MessageBox.Show($"DevBR could not start.\n\n{ex.Message}\n\nDetails were written to {paths.LogsDirectory}.", "DevBR", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.Services.GetRequiredService<WorkerProcessHost>().DisposeAsync();
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }

    private static IHost BuildHost(AppPaths paths)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Services.AddSerilog(Log.Logger, dispose: false);

        // Infrastructure
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        builder.Services.AddSingleton<StateDatabase>();
        builder.Services.AddSingleton<ActivityStore>();
        builder.Services.AddSingleton(new WorkerHostOptions());
        builder.Services.AddSingleton<WorkerProcessHost>();
        builder.Services.AddSingleton<WorkerArchiveService>();
        builder.Services.AddSingleton<IArchiveService>(sp => sp.GetRequiredService<WorkerArchiveService>());
        builder.Services.AddSingleton<BrokerLauncher>();

        // Presentation
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<Navigator>();
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<ArchiveSelfTest>();
        builder.Services.AddSingleton<OverviewViewModel>();
        builder.Services.AddSingleton<DiscoveryViewModel>();
        builder.Services.AddSingleton<BackupViewModel>();
        builder.Services.AddSingleton<RestoreViewModel>();
        builder.Services.AddSingleton<ActivityViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        return builder.Build();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception.");
        MessageBox.Show(
            $"Something went wrong, but DevBR is still running.\n\n{e.Exception.Message}\n\nDetails were written to the log.",
            "DevBR", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
