using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevBR.App.Controls;
using DevBR.App.Services;
using DevBR.Domain;
using DevBR.Infrastructure;
using DevBR.Infrastructure.State;
using DevBR.Infrastructure.Workers;

namespace DevBR.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ActivityStore _activity;

    public MainViewModel(
        Navigator navigator,
        WorkerProcessHost workerHost,
        ActivityStore activity,
        OverviewViewModel overview,
        DiscoveryViewModel discovery,
        BackupViewModel backup,
        RestoreViewModel restore,
        ActivityViewModel activityPage,
        SettingsViewModel settings)
    {
        _activity = activity;
        Pages = [overview, discovery, backup, restore, activityPage, settings];
        SelectedPage = overview;

        navigator.NavigationRequested += (_, type) => SelectedPage = Pages.First(p => p.GetType() == type);
        workerHost.Faulted += OnWorkerFaulted;
    }

    public ObservableCollection<PageViewModel> Pages { get; }

    [ObservableProperty]
    public partial PageViewModel SelectedPage { get; set; }

    [ObservableProperty]
    public partial bool IsBannerVisible { get; set; }

    [ObservableProperty]
    public partial string? BannerTitle { get; set; }

    [ObservableProperty]
    public partial string? BannerMessage { get; set; }

    [ObservableProperty]
    public partial InfoSeverity BannerSeverity { get; set; }

    public string BuildLabel => $"Version {BuildInfo.Version}";

    public bool IsDevelopmentBuild => BuildInfo.IsDevelopmentBuild;

    public string ElevationLabel => BuildInfo.IsElevated ? "Running as administrator" : "Standard user";

    partial void OnSelectedPageChanged(PageViewModel value) => _ = value.OnNavigatedToAsync();

    [RelayCommand]
    private void NavigateToIndex(string index)
    {
        if (int.TryParse(index, out var i) && i >= 0 && i < Pages.Count)
        {
            SelectedPage = Pages[i];
        }
    }

    [RelayCommand]
    private void DismissBanner() => IsBannerVisible = false;

    private void OnWorkerFaulted(object? sender, WorkerFaultedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            BannerSeverity = InfoSeverity.Error;
            BannerTitle = "Background archive process stopped";
            BannerMessage = e.Message;
            IsBannerVisible = true;
        });

        _ = _activity.AddAsync(EventSeverity.Error, "Worker", "The background archive process stopped unexpectedly.",
            e.ExitCode is { } code ? $"Exit code {code}" : null);
    }
}
