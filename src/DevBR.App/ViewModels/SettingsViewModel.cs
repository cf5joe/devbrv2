using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevBR.App.Services;
using DevBR.App.Theming;
using DevBR.Application.Settings;
using DevBR.Domain;
using DevBR.Infrastructure;
using DevBR.Infrastructure.State;
using DevBR.Infrastructure.Workers;
using DevBR.Ipc;

namespace DevBR.App.ViewModels;

public sealed record ThemeOption(ThemePreset Preset, string Name, string Description, Brush Background, Brush Surface, Brush Accent, Brush Text)
{
    // Lists without an item container announce ToString(); keep it a readable name, never a record dump.
    public override string ToString() => Name;
}

public sealed record AccentOption(string Name, string? Hex, Brush Fill, Brush Check)
{
    public override string ToString() => Name;
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly ISettingsStore _settings;
    private readonly ThemeService _theme;
    private readonly AppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly BrokerLauncher _broker;
    private readonly WorkerProcessHost _workerHost;
    private readonly WorkerArchiveService _worker;
    private readonly ArchiveSelfTest _selfTest;
    private readonly ActivityStore _activity;
    private readonly MachineContext _machine;
    private bool _loading;

    public SettingsViewModel(
        ISettingsStore settings,
        ThemeService theme,
        AppPaths paths,
        IDialogService dialogs,
        BrokerLauncher broker,
        WorkerProcessHost workerHost,
        WorkerArchiveService worker,
        ArchiveSelfTest selfTest,
        ActivityStore activity,
        MachineContext machine)
    {
        _settings = settings;
        _theme = theme;
        _paths = paths;
        _dialogs = dialogs;
        _broker = broker;
        _workerHost = workerHost;
        _worker = worker;
        _selfTest = selfTest;
        _activity = activity;
        _machine = machine;
        _machine.Changed += (_, _) => { OnPropertyChanged(nameof(MachineLabel)); OnPropertyChanged(nameof(IsSimulated)); };

        ThemeOptions =
        [
            Option(ThemePreset.Graphite, "Graphite", "Neutral charcoal with a blue accent", Palette.Graphite),
            Option(ThemePreset.Midnight, "Midnight", "Deep navy with a cyan accent", Palette.Midnight),
            Option(ThemePreset.Plum, "Plum", "Dark violet with a lavender accent", Palette.Plum),
            Option(ThemePreset.Light, "Light", "Bright surfaces with a blue accent", Palette.Light),
            Option(ThemePreset.FollowWindows, "Follow Windows", "Graphite or Light, matching your Windows app mode",
                ThemeService.WindowsUsesLightTheme() ? Palette.Light : Palette.Graphite),
        ];

        _theme.ThemeChanged += (_, _) => RebuildAccents();
        RebuildAccents();

        _loading = true;
        SelectedTheme = ThemeOptions.First(o => o.Preset == settings.Current.Theme);
        SelectedAccent = AccentOptions.FirstOrDefault(o => string.Equals(o.Hex, settings.Current.AccentHex, StringComparison.OrdinalIgnoreCase)) ?? AccentOptions[0];
        _loading = false;
    }

    public override string Title => "Settings";

    public override string Glyph => "";

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }

    public ObservableCollection<AccentOption> AccentOptions { get; } = [];

    [ObservableProperty]
    public partial ThemeOption? SelectedTheme { get; set; }

    [ObservableProperty]
    public partial AccentOption? SelectedAccent { get; set; }

    public bool IsHighContrast => _theme.IsHighContrast;

    public string DataFolder => _paths.Root;

    public string ScratchFolder => _settings.Current.ScratchDirectory ?? _paths.DefaultScratchDirectory;

    public bool HasCustomScratchFolder => _settings.Current.ScratchDirectory is not null;

    [ObservableProperty]
    public partial string? BrokerResult { get; set; }

    [ObservableProperty]
    public partial string? WorkerResult { get; set; }

    [ObservableProperty]
    public partial string? SelfTestStatus { get; set; }

    public ObservableCollection<SelfTestStep> SelfTestResults { get; } = [];

    public string VersionLabel => $"DevBR {BuildInfo.Version}";

    public string ChannelLabel => BuildInfo.IsDevelopmentBuild
        ? "Development build. Unsigned; not for distribution."
        : "Release build.";

    public bool IsDevelopmentBuild => BuildInfo.IsDevelopmentBuild;

    partial void OnSelectedThemeChanged(ThemeOption? value)
    {
        if (!_loading && value is not null)
        {
            _ = _settings.SaveAsync(_settings.Current with { Theme = value.Preset }, CancellationToken.None);
        }
    }

    partial void OnSelectedAccentChanged(AccentOption? value)
    {
        if (!_loading && value is not null)
        {
            _ = _settings.SaveAsync(_settings.Current with { AccentHex = value.Hex }, CancellationToken.None);
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.RevealFolder(_paths.Root);

    [RelayCommand]
    private async Task ChangeScratchFolderAsync()
    {
        var folder = _dialogs.PickFolder("Choose a scratch folder for large operations", ScratchFolder);
        if (folder is null)
        {
            return;
        }

        await _settings.SaveAsync(_settings.Current with { ScratchDirectory = folder }, CancellationToken.None);
        OnPropertyChanged(nameof(ScratchFolder));
        OnPropertyChanged(nameof(HasCustomScratchFolder));
    }

    [RelayCommand]
    private async Task ResetScratchFolderAsync()
    {
        await _settings.SaveAsync(_settings.Current with { ScratchDirectory = null }, CancellationToken.None);
        OnPropertyChanged(nameof(ScratchFolder));
        OnPropertyChanged(nameof(HasCustomScratchFolder));
    }

    [RelayCommand]
    private async Task TestBrokerAsync()
    {
        BrokerResult = "Waiting for administrator approval…";
        var result = await _broker.LaunchAsync(CancellationToken.None);
        if (result.Client is { } client)
        {
            BrokerResult = $"Connected and verified. Elevated: {(result.Status!.IsElevated ? "yes" : "no")}. Allowed operations: {string.Join(", ", result.Status.Operations)}.";
            await client.DisposeAsync();
        }
        else
        {
            BrokerResult = result.Message;
        }

        await _activity.AddAsync(result.Outcome == BrokerLaunchOutcome.Connected ? EventSeverity.Information : EventSeverity.Warning,
            "Privileged helper", $"Privileged helper test: {result.Outcome}.");
    }

    [RelayCommand]
    private async Task PingWorkerAsync()
    {
        try
        {
            var status = await _worker.PingAsync(CancellationToken.None);
            WorkerResult = $"Running (process {status.ProcessId}, version {status.Version}).";
        }
        catch (Exception ex) when (ex is IpcException or IOException or TimeoutException)
        {
            WorkerResult = $"Not available: {ex.Message}";
        }
    }

    /// <summary>Development builds only: terminates the worker to demonstrate that the GUI survives.</summary>
    [RelayCommand]
    private void SimulateWorkerFailure()
    {
        if (!BuildInfo.IsDevelopmentBuild || _workerHost.ProcessId is not { } pid)
        {
            WorkerResult = "The worker is not running. Use “Check worker” first.";
            return;
        }

        using var process = Process.GetProcessById(pid);
        process.Kill();
        WorkerResult = $"Terminated worker process {pid}.";
    }

    [RelayCommand]
    private async Task RunSelfTestAsync()
    {
        SelfTestResults.Clear();
        var progress = new Progress<string>(status => SelfTestStatus = status);
        SelfTestStatus = "Starting…";

        var scratch = ScratchFolder;
        Directory.CreateDirectory(scratch);
        var steps = await _selfTest.RunAsync(scratch, progress, CancellationToken.None);
        foreach (var step in steps)
        {
            SelfTestResults.Add(step);
        }

        var failed = steps.Count(s => !s.Passed);
        SelfTestStatus = failed == 0 ? $"All {steps.Count} checks passed." : $"{failed} of {steps.Count} checks failed.";
        await _activity.AddAsync(failed == 0 ? EventSeverity.Information : EventSeverity.Error, "Diagnostics", $"Archive self-test: {SelfTestStatus}");
    }

    // --- Simulated machines (development builds) --------------------------------------------------

    public string MachineLabel => _machine.Label;

    public bool IsSimulated => _machine.Current.IsSimulated;

    public string SimulationsFolder => _machine.SimulationsRoot;

    [ObservableProperty]
    public partial string? SimulationResult { get; set; }

    [RelayCommand]
    private async Task CreateSampleWorkstationAsync()
        => await SwitchToAsync(() => _machine.CreateSampleWorkstation(), "sample developer workstation");

    [RelayCommand]
    private async Task CreateCleanTargetAsync()
        => await SwitchToAsync(() => _machine.CreateCleanTarget(), "clean target computer");

    [RelayCommand]
    private async Task OpenSimulatedAsync()
    {
        Directory.CreateDirectory(_machine.SimulationsRoot);
        var folder = _dialogs.PickFolder("Open a simulated machine folder (contains machine.json)", _machine.SimulationsRoot);
        if (folder is null)
        {
            return;
        }

        if (!Simulation.SimulatedMachine.IsMachineFolder(folder))
        {
            SimulationResult = "That folder is not a simulated machine (machine.json is missing).";
            return;
        }

        await SwitchToAsync(() => folder, "simulated machine");
    }

    [RelayCommand]
    private async Task UseThisComputerAsync()
    {
        _machine.UseThisComputer();
        SimulationResult = "Now looking at this computer.";
        await _activity.AddAsync(EventSeverity.Information, "Simulation", "Switched back to this computer.");
    }

    private async Task SwitchToAsync(Func<string> prepare, string description)
    {
        try
        {
            var folder = await Task.Run(prepare);
            _machine.UseSimulated(folder);
            SimulationResult = $"Now looking at the {description} in {folder}. Run discovery to inventory it.";
            await _activity.AddAsync(EventSeverity.Information, "Simulation", $"Switched to simulated machine {_machine.Label}.", folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
        {
            SimulationResult = $"Could not open the simulated machine: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenNotices() => _dialogs.OpenDocument(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md"));

    private void RebuildAccents()
    {
        var palette = _theme.CurrentPalette ?? Palette.Graphite;
        var selectedHex = SelectedAccent?.Hex;
        var wasLoading = _loading;
        _loading = true;

        AccentOptions.Clear();
        foreach (var choice in AccentChoices.All)
        {
            var color = choice.Hex is null ? palette.Accent : ColorMath.Parse(choice.Hex);
            AccentOptions.Add(new AccentOption(
                choice.Hex is null ? $"{choice.Name} ({palette.Name})" : choice.Name,
                choice.Hex,
                Frozen(color),
                Frozen(ColorMath.ForegroundFor(color))));
        }

        SelectedAccent = AccentOptions.FirstOrDefault(o => o.Hex == selectedHex) ?? AccentOptions[0];
        _loading = wasLoading;
    }

    private static ThemeOption Option(ThemePreset preset, string name, string description, Palette palette)
        => new(preset, name, description, Frozen(palette.Background), Frozen(palette.Surface), Frozen(palette.Accent), Frozen(palette.Text));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
