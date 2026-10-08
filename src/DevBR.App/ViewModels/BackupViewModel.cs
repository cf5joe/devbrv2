using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevBR.App.Controls;
using DevBR.App.Services;
using DevBR.Application;
using DevBR.Application.Archive;
using DevBR.Application.Settings;
using DevBR.Backup;
using DevBR.Domain;
using DevBR.Infrastructure;
using DevBR.Infrastructure.State;
using Microsoft.Extensions.Logging;

namespace DevBR.App.ViewModels;

public sealed record WizardStep(int Number, string Title, string Description);

/// <param name="State">"Done", "Current" or "Upcoming"; shown with text and a glyph, not color alone.</param>
public sealed record StepIndicator(int Number, string Title, string State)
{
    public bool IsCurrent => State == "Current";

    public bool IsDone => State == "Done";

    public string AccessibleName => $"Step {Number}: {Title}, {State.ToLowerInvariant()}";

    // Lists without an item container announce ToString(); keep it a readable name, never a record dump.
    public override string ToString() => AccessibleName;
}

public sealed record SelectedItemSummary(string Owner, string Name, string? Badge)
{
    // Lists without an item container announce ToString(); keep it a readable name, never a record dump.
    public override string ToString() => Badge is null ? $"{Owner}: {Name}" : $"{Owner}: {Name}, {Badge}";
}

public sealed record FindingRow(InfoSeverity Severity, string Title, string? Remediation)
{
    // Lists without an item container announce ToString(); keep it a readable name, never a record dump.
    public override string ToString() => $"{Severity}: {Title}";
}

public sealed record CaptureRow(string Name, string Owner, string Detail, string? Status)
{
    // Lists without an item container announce ToString(); keep it a readable name, never a record dump.
    public override string ToString() => string.Join(", ", new[] { $"{Owner}: {Name}", Detail, Status }.Where(s => !string.IsNullOrEmpty(s)));
}

public sealed record ResultRow(string Name, string Status, string Detail, IReadOnlyList<string> Warnings)
{
    public bool IsComplete => Status == "Complete";

    // Lists without an item container announce ToString(); keep it a readable name, never a record dump.
    public override string ToString() => $"{Name}: {Status}, {Detail}";
}

public sealed partial class ExclusionRuleRow(ExclusionRule rule, Action changed) : ObservableObject
{
    public string FolderName => rule.FolderName;

    public string Description => rule.Description;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = rule.Enabled;

    public ExclusionRule ToRule() => rule with { Enabled = Enabled };

    // Lists without an item container announce ToString(); keep it a readable name, never a record dump.
    public override string ToString() => FolderName;

    partial void OnEnabledChanged(bool value) => changed();
}

/// <summary>
/// The six-step backup wizard: select, add files, review, output, confirm the exact plan, then run with
/// progress and a final report. The plan shown in step 5 is exactly what is captured.
/// </summary>
public sealed partial class BackupViewModel : PageViewModel
{
    public const int MinimumPasswordLength = 8;

    private readonly CatalogSession _session;
    private readonly Navigator _navigator;
    private readonly DiscoveryViewModel _discovery;
    private readonly MachineContext _machine;
    private readonly IDialogService _dialogs;
    private readonly ISettingsStore _settings;
    private readonly AppPaths _paths;
    private readonly IArchiveService _archive;
    private readonly ActivityStore _activity;
    private readonly ILoggerFactory _loggers;
    private CancellationTokenSource? _work;
    private SecretText? _password;
    private string? _passwordConfirmation;

    public BackupViewModel(CatalogSession session, Navigator navigator, DiscoveryViewModel discovery, MachineContext machine, IDialogService dialogs,
        ISettingsStore settings, AppPaths paths, IArchiveService archive, ActivityStore activity, ILoggerFactory loggers)
    {
        _session = session;
        _navigator = navigator;
        _discovery = discovery;
        _machine = machine;
        _dialogs = dialogs;
        _settings = settings;
        _paths = paths;
        _archive = archive;
        _activity = activity;
        _loggers = loggers;

        foreach (var rule in DefaultExclusions.All)
        {
            Rules.Add(new ExclusionRuleRow(rule, InvalidatePlan));
        }

        OnStepChanged(Step);

        _session.SnapshotChanged += (_, _) => Dispatch(RefreshSelection);
        _session.SelectionChanged += (_, _) => Dispatch(RefreshSelection);
        _machine.Changed += (_, _) => Dispatch(StartOver);
    }

    public override string Title => "Backup";

    public override string Glyph => "";

    public IReadOnlyList<WizardStep> Steps { get; } =
    [
        new(1, "Select", "Items chosen on the Discovery page."),
        new(2, "Add files", "Your own files and folders."),
        new(3, "Review", "Exclusions, sensitive items, warnings."),
        new(4, "Output", "File, compression and encryption."),
        new(5, "Confirm", "The exact capture plan."),
        new(6, "Back up", "Capture, verify and report."),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4), nameof(IsStep5), nameof(IsStep6), nameof(CanGoBack))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand))]
    public partial int Step { get; set; } = 1;

    public ObservableCollection<StepIndicator> StepIndicators { get; } = [];

    partial void OnStepChanged(int value)
    {
        StepIndicators.Clear();
        foreach (var step in Steps)
        {
            StepIndicators.Add(new StepIndicator(step.Number, step.Title, step.Number < value ? "Done" : step.Number == value ? "Current" : "Upcoming"));
        }
    }

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsStep4 => Step == 4;
    public bool IsStep5 => Step == 5;
    public bool IsStep6 => Step == 6;

    public bool CanGoBack => Step is > 1 and < 6;

    // --- Step 1 -----------------------------------------------------------------------------------

    public ObservableCollection<SelectedItemSummary> Selected { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool HasDiscovery { get; set; }

    [ObservableProperty]
    public partial string? SelectionSummary { get; set; }

    // --- Step 2 -----------------------------------------------------------------------------------

    public ObservableCollection<string> CustomPaths { get; } = [];

    // --- Step 3 -----------------------------------------------------------------------------------

    public ObservableCollection<ExclusionRuleRow> Rules { get; } = [];

    public ObservableCollection<FindingRow> Findings { get; } = [];

    public ObservableCollection<CaptureRow> AppliedExclusions { get; } = [];

    public ObservableCollection<string> EncryptionReasons { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial BackupPlan? Plan { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool IsPlanning { get; set; }

    [ObservableProperty]
    public partial string? PlanStatus { get; set; }

    [ObservableProperty]
    public partial string? PlanSummary { get; set; }

    // --- Step 4 -----------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputExists))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial string? OutputPath { get; set; }

    public bool OutputExists => OutputPath is not null && File.Exists(OutputPath);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool AllowOverwrite { get; set; }

    public IReadOnlyList<CompressionPreset> CompressionOptions { get; } = [CompressionPreset.Fast, CompressionPreset.Normal, CompressionPreset.Maximum, CompressionPreset.Store];

    [ObservableProperty]
    public partial CompressionPreset Compression { get; set; } = CompressionPreset.Normal;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDisclosure))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    public partial bool Encrypt { get; set; }

    [ObservableProperty]
    public partial bool EncryptionForced { get; set; }

    [ObservableProperty]
    public partial string? PasswordError { get; set; }

    public bool ShowDisclosure => !Encrypt;

    public string ScratchFolder => _settings.Current.ScratchDirectory ?? _paths.DefaultScratchDirectory;

    public string Disclosure => BackupRunner.Disclosure;

    // --- Step 5 -----------------------------------------------------------------------------------

    public ObservableCollection<CaptureRow> Capture { get; } = [];

    [ObservableProperty]
    public partial string? ConfirmSummary { get; set; }

    // --- Step 6 -----------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string? RunStage { get; set; }

    [ObservableProperty]
    public partial string? RunItem { get; set; }

    [ObservableProperty]
    public partial string? RunCounts { get; set; }

    [ObservableProperty]
    public partial double RunPercent { get; set; }

    [ObservableProperty]
    public partial bool RunIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial BackupResult? Result { get; set; }

    [ObservableProperty]
    public partial InfoSeverity ResultSeverity { get; set; }

    [ObservableProperty]
    public partial string? ResultTitle { get; set; }

    [ObservableProperty]
    public partial string? ResultDetail { get; set; }

    public ObservableCollection<ResultRow> ResultRows { get; } = [];

    public override async Task OnNavigatedToAsync()
    {
        await _discovery.OnNavigatedToAsync();
        RefreshSelection();
    }

    // --- Navigation ------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        switch (Step)
        {
            case 2:
                Step = 3;
                await PreparePlanAsync();
                return;
            case 3:
                Step = 4;
                OutputPath ??= SuggestOutputPath();
                EncryptionForced = Plan?.RequiresEncryption == true;
                if (EncryptionForced)
                {
                    Encrypt = true;
                }

                return;
            case 4:
                if (!ValidatePassword())
                {
                    return;
                }

                // Re-plan with the chosen output protected, so the backup can never include itself.
                await PreparePlanAsync();
                if (Plan is null)
                {
                    return;
                }

                BuildConfirmation();
                Step = 5;
                return;
            case 5:
                Step = 6;
                await RunBackupAsync();
                return;
            default:
                Step++;
                return;
        }
    }

    private bool CanGoNext() => Step switch
    {
        1 => HasDiscovery && _session.SelectedArtifacts.Count > 0,
        3 => !IsPlanning && Plan is not null,
        4 => !string.IsNullOrWhiteSpace(OutputPath) && (!OutputExists || AllowOverwrite),
        5 => Plan is not null,
        6 => false,
        _ => true,
    };

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step--;

    [RelayCommand]
    private void GoToDiscovery() => _navigator.NavigateTo<DiscoveryViewModel>();

    [RelayCommand]
    private void ReviewSelection()
    {
        _discovery.Tab = DiscoveryTab.Backup;
        _navigator.NavigateTo<DiscoveryViewModel>();
    }

    [RelayCommand]
    private void StartOver()
    {
        _work?.Cancel();
        Step = 1;
        Plan = null;
        Result = null;
        ResultRows.Clear();
        _password = null;
        _passwordConfirmation = null;
        AllowOverwrite = false;
        OutputPath = null;
        RefreshSelection();
    }

    // --- Step 2 ----------------------------------------------------------------------------------

    [RelayCommand]
    private void AddFolder()
    {
        if (_dialogs.PickFolder("Add a folder to the backup", null) is { } folder)
        {
            AddCustom(folder);
        }
    }

    [RelayCommand]
    private void AddFiles()
    {
        foreach (var file in _dialogs.PickFiles("Add files to the backup"))
        {
            AddCustom(file);
        }
    }

    [RelayCommand]
    private void RemoveCustom(string? path)
    {
        if (path is not null && CustomPaths.Remove(path))
        {
            InvalidatePlan();
        }
    }

    private void AddCustom(string path)
    {
        if (!CustomPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            CustomPaths.Add(path);
            InvalidatePlan();
        }
    }

    // --- Step 3 ----------------------------------------------------------------------------------

    [RelayCommand]
    private Task ReplanAsync() => PreparePlanAsync();

    private void InvalidatePlan()
    {
        // Any change that can add effects invalidates the reviewed plan.
        Plan = null;
        if (Step == 3)
        {
            PlanStatus = "The selection or rules changed. Prepare the plan again to review it.";
        }
    }

    private async Task PreparePlanAsync()
    {
        if (_session.Snapshot is not { } snapshot)
        {
            return;
        }

        _work?.Cancel();
        using var cts = new CancellationTokenSource();
        _work = cts;
        IsPlanning = true;
        PlanStatus = "Preparing the plan…";
        Findings.Clear();
        AppliedExclusions.Clear();
        EncryptionReasons.Clear();

        var request = new BackupPlanRequest(_machine.Current, snapshot, _session.SelectedArtifacts, [.. CustomPaths],
            [.. Rules.Select(r => r.ToRule())], ProtectedPaths());
        var progress = new Progress<string>(s => PlanStatus = s);

        try
        {
            Plan = await Task.Run(() => new BackupPlanner().Plan(request, progress, cts.Token), cts.Token);
            ShowPlan(Plan);
            PlanStatus = null;
        }
        catch (OperationCanceledException)
        {
            PlanStatus = "Planning was cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PlanStatus = $"The plan could not be prepared: {ex.Message}";
        }
        finally
        {
            IsPlanning = false;
            if (ReferenceEquals(_work, cts))
            {
                _work = null;
            }
        }
    }

    /// <summary>DevBR's data, the scratch folder and the output file are never captured into the backup itself.</summary>
    private List<string> ProtectedPaths()
    {
        var paths = new List<string> { _paths.Root, ScratchFolder };
        if (OutputPath is not null)
        {
            paths.Add(OutputPath);
        }

        return paths;
    }

    private void ShowPlan(BackupPlan plan)
    {
        PlanSummary = $"{Formatting.Count(plan.Artifacts.Count(a => !a.Blocked), "item", "items")} · {Formatting.Count(plan.Files, "file", "files")} · {Formatting.Bytes(plan.Bytes)} before compression";

        foreach (var finding in plan.Findings.OrderByDescending(f => f.Level))
        {
            Findings.Add(new FindingRow(finding.Level switch
            {
                FindingLevel.Blocking => InfoSeverity.Error,
                FindingLevel.Warning => InfoSeverity.Warning,
                _ => InfoSeverity.Information,
            }, finding.Message, finding.Remediation));
        }

        foreach (var exclusion in plan.Artifacts.SelectMany(a => a.Exclusions))
        {
            AppliedExclusions.Add(new CaptureRow(exclusion.Path, exclusion.Rule, $"{Formatting.Count(exclusion.Files, "file", "files")}, {Formatting.Bytes(exclusion.Bytes)}", null));
        }

        foreach (var reason in plan.EncryptionReasons)
        {
            EncryptionReasons.Add(reason);
        }
    }

    // --- Step 4 ----------------------------------------------------------------------------------

    [RelayCommand]
    private void BrowseOutput()
    {
        if (_dialogs.PickSaveBackupFile(OutputPath ?? SuggestOutputPath()) is { } path)
        {
            OutputPath = path;
            AllowOverwrite = false;
        }
    }

    /// <summary>Called by the view; PasswordBox values cannot be data-bound.</summary>
    public void SetPassword(string password, string confirmation)
    {
        _password = string.IsNullOrEmpty(password) ? null : new SecretText(password);
        _passwordConfirmation = confirmation;
        PasswordError = null;
    }

    private bool ValidatePassword()
    {
        if (!Encrypt)
        {
            _password = null;
            return true;
        }

        if (_password is null || _password.Reveal().Length < MinimumPasswordLength)
        {
            PasswordError = $"Use a password of at least {MinimumPasswordLength} characters.";
            return false;
        }

        if (!string.Equals(_password.Reveal(), _passwordConfirmation, StringComparison.Ordinal))
        {
            PasswordError = "The passwords do not match.";
            return false;
        }

        PasswordError = null;
        return true;
    }

    partial void OnEncryptChanged(bool value)
    {
        if (!value && EncryptionForced)
        {
            Encrypt = true;
        }
    }

    private string SuggestOutputPath()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var name = $"{_machine.Current.Info.ComputerName}-{DateTime.Now:yyyy-MM-dd-HHmm}{ArchiveContract.FileExtension}";
        return Path.Combine(documents, "DevBR Backups", name);
    }

    // --- Step 5 ----------------------------------------------------------------------------------

    private void BuildConfirmation()
    {
        Capture.Clear();
        if (Plan is null)
        {
            return;
        }

        foreach (var artifact in Plan.Artifacts)
        {
            var detail = artifact.Kind switch
            {
                CaptureKind.InventoryOnly => "Inventory records only",
                CaptureKind.Environment => "Variables with their raw values and registry types",
                _ => $"{Formatting.Count(artifact.FileCount, "file", "files")}, {Formatting.Bytes(artifact.Bytes)}",
            };
            Capture.Add(new CaptureRow(artifact.Artifact.DisplayName, DiscoveryText.Owner(artifact.Artifact.OwnerToolId), detail, artifact.Blocked ? "Blocked — will be skipped" : null));
        }

        ConfirmSummary = $"{PlanSummary}. Saved to {OutputPath}{(Encrypt ? ", encrypted with AES-256 (file names hidden)" : ", not encrypted")}.";
    }

    // --- Step 6 ----------------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel()
    {
        RunStage = "Cancelling — finishing the current file…";
        _work?.Cancel();
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (Result?.OutputPath is { } path && Path.GetDirectoryName(path) is { } folder)
        {
            _dialogs.RevealFolder(folder);
        }
    }

    private async Task RunBackupAsync()
    {
        if (Plan is null || OutputPath is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath)!);
        using var cts = new CancellationTokenSource();
        _work = cts;
        IsRunning = true;
        Result = null;
        ResultRows.Clear();
        RunIndeterminate = true;
        RunStage = "Starting…";

        var options = new BackupRunOptions(OutputPath, ScratchFolder, Compression, Encrypt ? _password : null, AllowOverwrite);
        var progress = new Progress<OperationEvent>(OnProgress);

        try
        {
            var runner = new BackupRunner(_archive, _loggers.CreateLogger<BackupRunner>());
            var result = await runner.RunAsync(Plan, options, progress, cts.Token);
            ShowResult(result);
            await _activity.AddAsync(
                result.Outcome is BackupOutcome.Succeeded or BackupOutcome.SucceededWithWarnings ? EventSeverity.Information : EventSeverity.Warning,
                "Backup", $"Backup {result.Outcome}: {result.Message}", result.OutputPath);
        }
        finally
        {
            IsRunning = false;
            _work = null;
            _password = null; // never kept longer than needed
        }
    }

    private void OnProgress(OperationEvent e)
    {
        RunStage = e.Stage switch
        {
            OperationStage.Staging => "Copying selected files",
            OperationStage.Compression => "Compressing",
            OperationStage.Verification => "Verifying every file in the backup",
            _ => e.Message,
        };
        RunItem = e.ItemId;

        if (e.BytesTotal is > 0 and var total)
        {
            RunIndeterminate = false;
            RunPercent = Math.Min(100, e.BytesProcessed * 100.0 / total);
        }

        var parts = new List<string>();
        if (e.FilesTotal is { } files)
        {
            parts.Add($"{e.FilesProcessed.ToString("N0", CultureInfo.CurrentCulture)} of {files.ToString("N0", CultureInfo.CurrentCulture)} files");
        }

        if (e.BytesTotal is { } bytes)
        {
            parts.Add($"{Formatting.Bytes(e.BytesProcessed)} of {Formatting.Bytes(bytes)}");
        }

        if (e.Elapsed > TimeSpan.Zero)
        {
            parts.Add($"{e.Elapsed:mm\\:ss} elapsed");
            if (e.BytesProcessed > 0 && e.Stage == OperationStage.Staging)
            {
                parts.Add($"{Formatting.Bytes((long)(e.BytesProcessed / e.Elapsed.TotalSeconds))}/s");
            }
        }

        if (e.EstimatedRemaining is { } eta && e.EtaConfidence != EtaConfidence.None)
        {
            parts.Add(e.EtaConfidence == EtaConfidence.High ? $"about {eta:mm\\:ss} left" : $"roughly {eta:mm\\:ss} left");
        }

        RunCounts = string.Join(" · ", parts);
    }

    private void ShowResult(BackupResult result)
    {
        Result = result;
        (ResultSeverity, ResultTitle) = result.Outcome switch
        {
            BackupOutcome.Succeeded => (InfoSeverity.Success, "Backup created and verified"),
            BackupOutcome.SucceededWithWarnings => (InfoSeverity.Warning, "Backup created and verified, with warnings"),
            BackupOutcome.Cancelled => (InfoSeverity.Information, "Backup cancelled"),
            BackupOutcome.EncryptionRequired => (InfoSeverity.Warning, "Encryption is required"),
            _ => (InfoSeverity.Error, "The backup failed"),
        };

        ResultDetail = result.OutputPath is null
            ? result.Message
            : $"{result.Message} {Formatting.Count(result.Files, "file", "files")} ({Formatting.Bytes(result.Bytes)}) in {Formatting.Bytes(result.ArchiveBytes)} at {result.OutputPath}. Took {result.Duration.TotalSeconds:0.0} s.";

        foreach (var artifact in result.Artifacts)
        {
            var kind = Plan?.Artifacts.FirstOrDefault(a => a.Artifact.Id == artifact.ArtifactId)?.Kind;
            var detail = kind == CaptureKind.InventoryOnly
                ? "Inventory records"
                : $"{Formatting.Count(artifact.Files, "file", "files")}, {Formatting.Bytes(artifact.Bytes)}";
            ResultRows.Add(new ResultRow(artifact.DisplayName, artifact.Status, detail, artifact.Warnings));
        }
    }

    private void RefreshSelection()
    {
        HasDiscovery = _session.Snapshot is not null;
        Selected.Clear();
        foreach (var artifact in _session.SelectedArtifacts)
        {
            var badge = artifact.Sensitivity switch
            {
                Sensitivity.Credential => "Credential",
                Sensitivity.ContainsRecognizedSecrets => "Contains secrets",
                Sensitivity.MayContainSecrets => "May contain secrets",
                _ => null,
            };
            Selected.Add(new SelectedItemSummary(DiscoveryText.Owner(artifact.OwnerToolId), artifact.DisplayName, badge));
        }

        SelectionSummary = HasDiscovery ? $"{Formatting.Count(Selected.Count, "item", "items")} selected for backup." : null;
        if (Step > 1 && Step < 6)
        {
            InvalidatePlan();
        }

        NextCommand.NotifyCanExecuteChanged();
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }
}

public static class BackupLabels
{
    public static System.Windows.Data.IValueConverter NextLabel { get; } = new Converter(v => v switch
    {
        2 => "Review",
        3 => "Continue",
        4 => "Review the plan",
        5 => "Back up now",
        _ => "Next",
    });

    public static System.Windows.Data.IValueConverter Not { get; } = new Converter(v => v is not true);

    private sealed class Converter(Func<object?, object> convert) : System.Windows.Data.IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
