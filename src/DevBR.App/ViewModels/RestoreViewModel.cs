using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevBR.App.Services;
using DevBR.Application;
using DevBR.Application.Archive;
using DevBR.Application.Settings;
using DevBR.App.Controls;
using DevBR.Backup;
using DevBR.Restore;
using DevBR.Domain;
using DevBR.Infrastructure;
using DevBR.Infrastructure.State;

namespace DevBR.App.ViewModels;

public sealed record DetailRow(string Label, string Value);

public enum RestoreStage
{
    ChooseFile,
    Inspecting,
    NeedsPassword,
    Overview,
    Failed,
}

public sealed record BackupArtifactRow(ArtifactSummary Summary)
{
    public string Key => Summary.Record.Key;

    public string Name => Summary.Record.Artifact.DisplayName;

    public string Owner => DiscoveryText.Owner(Summary.Record.Artifact.OwnerToolId);

    public string Status => Summary.Record.Status;

    public bool IsComplete => Status == "Complete";

    public string Detail => Summary.Record.Kind switch
    {
        CaptureKind.InventoryOnly => "Inventory",
        CaptureKind.Environment => "Environment variables",
        _ => $"{Formatting.Count(Summary.EntryCount, "file", "files")} · {Formatting.Bytes(Summary.Bytes)}",
    };

    public IReadOnlyList<string> Warnings => Summary.Record.Warnings;
}

public sealed record BackupEntryRow(string Path, string Size);

/// <summary>
/// Restore steps 1–3: choose a backup, unlock it if encrypted, and browse its overview. Only the indexes
/// are extracted (by the archive worker, into a private folder) and verified; nothing is restored or run.
/// </summary>
public sealed partial class RestoreViewModel(IArchiveService archive, IDialogService dialogs, ActivityStore activity, ISettingsStore settings, AppPaths paths,
    RestorePlanner planner, MachineContext machine) : PageViewModel
{
    private const int EntriesPerPage = 200;

    private CancellationTokenSource? _inspection;
    private BackupOverview? _overview;
    private readonly Dictionary<string, RootMapping> _userMappings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConflictDecision> _decisions = new(StringComparer.Ordinal);
    private PlanApproval? _approval;
    private CancellationTokenSource? _preflightCts;

    // --- Restore planning (steps 4–7: destinations, preflight, review, approval) ---------------

    public ObservableCollection<RestoreItemRow> Items { get; } = [];

    public ObservableCollection<MappingRow> MappingRows { get; } = [];

    public ObservableCollection<FindingView> Findings { get; } = [];

    public ObservableCollection<OperationGroup> Groups { get; } = [];

    public ObservableCollection<string> Rewrites { get; } = [];

    public ObservableCollection<string> Reinstall { get; } = [];

    public ObservableCollection<string> McpServers { get; } = [];

    public string TargetLabel => machine.Label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreflight))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    public partial RestorePreflight? Preflight { get; set; }

    public bool HasPreflight => Preflight is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPreflightCommand), nameof(ApproveCommand))]
    public partial bool IsPreflighting { get; set; }

    [ObservableProperty]
    public partial string? PreflightStatus { get; set; }

    [ObservableProperty]
    public partial string? PreflightSummary { get; set; }

    [ObservableProperty]
    public partial string? ApprovalStatus { get; set; }

    [ObservableProperty]
    public partial InfoSeverity ApprovalSeverity { get; set; }

    [RelayCommand(CanExecute = nameof(CanRunPreflight))]
    private Task RunPreflightAsync() => PreflightAsync();

    private bool CanRunPreflight() => !IsPreflighting && _overview is not null;

    [RelayCommand]
    private async Task ChangeMappingAsync(MappingRow? row)
    {
        if (row is null || dialogs.PickFolder($"Where should {row.Source} go on this computer?", null) is not { } folder)
        {
            return;
        }

        _userMappings[row.Source] = new RootMapping(row.Source, folder, PathMappingOrigin.UserSelected);
        await PreflightAsync();
    }

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync()
    {
        if (Preflight is null)
        {
            return;
        }

        _approval = PlanApproval.Approve(Preflight);
        var count = Preflight.Effects.Count();
        ApprovalSeverity = InfoSeverity.Success;
        ApprovalStatus = $"Plan approved: {count} change{(count == 1 ? string.Empty : "s")} on {machine.Label}. Blocked items are left out until you recheck them. Restore execution is not included in this build yet.";
        await activity.AddAsync(EventSeverity.Information, "Restore", $"Approved a restore plan for {FileName} with {count} changes.", Preflight.Plan.ApprovalHash);
    }

    private bool CanApprove() => Preflight is not null && !IsPreflighting && Preflight.Effects.Any();

    private async Task PreflightAsync()
    {
        if (_overview is null)
        {
            return;
        }

        _preflightCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _preflightCts = cts;
        IsPreflighting = true;
        PreflightStatus = "Checking this computer…";

        try
        {
            var work = settings.Current.ScratchDirectory ?? paths.DefaultScratchDirectory;
            Directory.CreateDirectory(work);
            var request = new RestoreRequest(_overview, _password, machine.Current, Items.Where(i => i.IsSelected).Select(i => i.Key).ToHashSet(StringComparer.Ordinal),
                [.. _userMappings.Values], new Dictionary<string, ConflictDecision>(_decisions), work);
            var result = await planner.PreflightAsync(request, new Progress<string>(s => PreflightStatus = s), cts.Token);
            ShowPreflight(result);
            PreflightStatus = null;
        }
        catch (OperationCanceledException)
        {
            PreflightStatus = "Preflight was cancelled.";
        }
        catch (Exception ex) when (ex is ArchiveException or BackupFormatException or IOException or UnauthorizedAccessException)
        {
            PreflightStatus = $"Preflight could not finish: {ex.Message}";
        }
        finally
        {
            IsPreflighting = false;
            if (ReferenceEquals(_preflightCts, cts))
            {
                _preflightCts = null;
            }
        }
    }

    private void ShowPreflight(RestorePreflight result)
    {
        Preflight = result;

        MappingRows.Clear();
        foreach (var mapping in result.Mappings.DistinctBy(m => m.SourceAbsolutePath, StringComparer.OrdinalIgnoreCase))
        {
            MappingRows.Add(new MappingRow(mapping.SourceAbsolutePath, mapping.TargetRoot.Length == 0 ? "—" : mapping.TargetRoot, mapping.Origin switch
            {
                PathMappingOrigin.KnownFolder => "Your profile",
                PathMappingOrigin.UserSelected => "Chosen by you",
                _ => "Same location",
            }, mapping.Status == PathMappingStatus.Valid, mapping.SourceRoot.Root is LogicalRootKind.CustomRoot or LogicalRootKind.RepositoryRoot));
        }

        Findings.Clear();
        foreach (var finding in result.Findings.OrderByDescending(f => f.Severity))
        {
            Findings.Add(FindingView.From(finding));
        }

        Groups.Clear();
        var rows = result.Operations.Select(o => new OperationRow(o, OnDecisionChanged)).ToList();
        void Group(string title, string description, Func<OperationRow, bool> predicate)
        {
            var members = rows.Where(predicate).ToList();
            if (members.Count > 0)
            {
                Groups.Add(new OperationGroup(title, description, members));
            }
        }

        Group("Before you restore", "Steps for you to complete; DevBR rechecks them.", r => r.Operation.Operation.Action == RestoreAction.ManualStep);
        Group("Settings and files", "Created, merged, replaced or kept. Conflicts keep this computer's values unless you choose otherwise.",
            r => r.Operation.Operation.Action is RestoreAction.CreateFile or RestoreAction.ReplaceFile or RestoreAction.MergeStructuredSettings or RestoreAction.RestoreAlongside
                 || (r.Operation.Operation.Action == RestoreAction.Skip && r.HasChoice && !r.Id.Contains(":env:", StringComparison.Ordinal)));
        Group("Repositories", "Restored into new, empty folders with full history.", r => r.Operation.Operation.Action == RestoreAction.RestoreRepository);
        Group("System changes", "Environment variables and PATH. Changes to machine scope need administrator approval.",
            r => r.Operation.Operation.Action is RestoreAction.SetEnvironmentVariable or RestoreAction.AppendPathEntry || r.Id.Contains(":env:", StringComparison.Ordinal));
        Group("Installations and downloads", "Run only after you approve. Installers cannot be rolled back.", r => r.Operation.Operation.Action == RestoreAction.InstallDependency);

        var identical = result.Operations.Count(o => o.Operation.Action == RestoreAction.Skip && o.AllowedDecisions.Count == 0);
        PreflightSummary = $"{result.Effects.Count()} changes planned · {identical} already identical · {result.Findings.Count(f => f.Severity == FindingSeverity.Blocking)} blocking · {result.Findings.Count(f => f.Severity == FindingSeverity.Warning)} warnings";

        Replace(Rewrites, result.Rewrites.Where(r => r.Outcome != RewriteOutcome.Unchanged).Select(r => r.Outcome == RewriteOutcome.Rewritten
            ? $"{r.Field}: {r.Before} → {r.After}"
            : $"{r.Field}: {r.Before} (no destination; left unchanged)"));
        Replace(Reinstall, result.Reinstall.Select(r => $"{r.Name}{(r.SourceVersion is null ? string.Empty : " " + r.SourceVersion)} — {r.Hint}"));
        Replace(McpServers, result.McpServers.Select(s => $"{s.Name} ({s.File}): {s.Detail}"));

        // An approval stands only while the plan adds no effect the user has not seen.
        if (_approval is not null)
        {
            if (_approval.Covers(result))
            {
                ApprovalSeverity = InfoSeverity.Success;
                ApprovalStatus = "Your approval still covers this plan: nothing new was added.";
            }
            else
            {
                _approval = null;
                ApprovalSeverity = InfoSeverity.Warning;
                ApprovalStatus = "The plan now includes changes you have not approved. Review them and approve again.";
            }
        }
    }

    private void OnDecisionChanged(OperationRow row)
    {
        if (row.Decision is { } decision)
        {
            _decisions[row.Id] = decision;
            _ = PreflightAsync();
        }
    }

    private void OnSelectionChanged()
    {
        if (Preflight is not null)
        {
            PreflightSummary = "The selection changed. Run preflight again to update the plan.";
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    /// <summary>Kept in memory only for the next steps of this restore session.</summary>
    private SecretText? _password;

    public override string Title => "Restore";

    public override string Glyph => "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChooseFile), nameof(IsInspecting), nameof(IsNeedsPassword), nameof(IsOverview), nameof(IsFailed))]
    public partial RestoreStage Stage { get; set; } = RestoreStage.ChooseFile;

    [ObservableProperty]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial string? FileName { get; set; }

    [ObservableProperty]
    public partial string? PasswordError { get; set; }

    [ObservableProperty]
    public partial string? ErrorTitle { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsEncrypted { get; set; }

    public ObservableCollection<DetailRow> Details { get; } = [];

    public ObservableCollection<string> CaptureWarnings { get; } = [];

    public ObservableCollection<BackupArtifactRow> Artifacts { get; } = [];

    public ObservableCollection<BackupEntryRow> Entries { get; } = [];

    [ObservableProperty]
    public partial BackupArtifactRow? SelectedArtifact { get; set; }

    [ObservableProperty]
    public partial string? EntriesNote { get; set; }

    public bool IsChooseFile => Stage == RestoreStage.ChooseFile;

    public bool IsInspecting => Stage == RestoreStage.Inspecting;

    public bool IsNeedsPassword => Stage == RestoreStage.NeedsPassword;

    public bool IsOverview => Stage == RestoreStage.Overview;

    public bool IsFailed => Stage == RestoreStage.Failed;

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = dialogs.PickBackupFile();
        if (path is null)
        {
            return;
        }

        await OpenAsync(path);
    }

    /// <summary>Opens a backup directly (also used by tests and automation).</summary>
    public async Task OpenAsync(string path)
    {
        CloseOverview();
        FilePath = path;
        FileName = Path.GetFileName(path);
        _password = null;
        PasswordError = null;
        await InspectAsync(null);
    }

    [RelayCommand]
    private async Task SubmitPasswordAsync(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            PasswordError = "Enter the password that was set when this backup was created.";
            return;
        }

        await InspectAsync(new SecretText(password));
    }

    [RelayCommand]
    private void CancelInspection() => _inspection?.Cancel();

    [RelayCommand]
    private void StartOver()
    {
        CloseOverview();
        _password = null;
        FilePath = null;
        FileName = null;
        Stage = RestoreStage.ChooseFile;
    }

    partial void OnSelectedArtifactChanged(BackupArtifactRow? value)
    {
        Entries.Clear();
        EntriesNote = null;
        if (value is null || _overview is null)
        {
            return;
        }

        foreach (var entry in BackupReader.ReadEntries(_overview, value.Key, 0, EntriesPerPage))
        {
            Entries.Add(new BackupEntryRow(
                entry.EntryType == ArchiveEntryType.Directory ? entry.RelativePath + @"\" : entry.RelativePath.Length == 0 ? Path.GetFileName(entry.ArchivePath) : entry.RelativePath,
                entry.EntryType == ArchiveEntryType.Directory ? "folder" : Formatting.Bytes(entry.Size)));
        }

        if (value.Summary.EntryCount > EntriesPerPage)
        {
            EntriesNote = $"Showing the first {EntriesPerPage:N0} of {value.Summary.EntryCount:N0} files.";
        }
    }

    private async Task InspectAsync(SecretText? password)
    {
        if (FilePath is null)
        {
            return;
        }

        _inspection?.Cancel();
        using var cts = new CancellationTokenSource();
        _inspection = cts;
        Stage = RestoreStage.Inspecting;

        try
        {
            var scratch = settings.Current.ScratchDirectory ?? paths.DefaultScratchDirectory;
            Directory.CreateDirectory(scratch);
            var overview = await new BackupReader(archive).OpenAsync(FilePath, password, scratch, cts.Token);

            _password = password;
            PasswordError = null;
            ShowOverview(overview);
            await activity.AddAsync(EventSeverity.Information, "Restore", $"Opened backup {FileName}.");
        }
        catch (OperationCanceledException)
        {
            Stage = password is null ? RestoreStage.ChooseFile : RestoreStage.NeedsPassword;
        }
        catch (ArchiveException ex) when (ex.Kind == ArchiveErrorKind.PasswordRequired)
        {
            PasswordError = null;
            Stage = RestoreStage.NeedsPassword;
        }
        catch (ArchiveException ex) when (ex.Kind == ArchiveErrorKind.WrongPasswordOrCorrupt && password is not null)
        {
            PasswordError = "That password did not open this backup. Check it and try again. If the password is right, the file may be damaged.";
            Stage = RestoreStage.NeedsPassword;
        }
        catch (ArchiveException ex)
        {
            var (title, message) = Describe(ex);
            Fail(title, message);
            await activity.AddAsync(EventSeverity.Warning, "Restore", $"Could not open backup {FileName}: {title}.", ex.Kind.ToString());
        }
        catch (BackupFormatException ex)
        {
            Fail("This backup cannot be used", $"{ex.Message} DevBR will not restore from this file.");
            await activity.AddAsync(EventSeverity.Warning, "Restore", $"Rejected backup {FileName}: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_inspection, cts))
            {
                _inspection = null;
            }
        }
    }

    private void ShowOverview(BackupOverview overview)
    {
        _overview = overview;
        var manifest = overview.Manifest;
        IsEncrypted = overview.Encrypted;

        Details.Clear();
        Details.Add(new("Source computer", manifest.SourceMachineName));
        Details.Add(new("Source system", $"{manifest.SourceOsDescription} ({manifest.SourceArchitecture})"));
        Details.Add(new("Created", manifest.CreatedAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)));
        Details.Add(new("Created with", $"DevBR {manifest.AppVersion}"));
        Details.Add(new("Archive format", manifest.FormatVersion.ToString()));
        Details.Add(new("Encryption", overview.Encrypted ? "AES-256 with encrypted file names" : "Not encrypted"));
        Details.Add(new("Contents", $"{Formatting.Count(manifest.Totals.ArtifactCount, "item", "items")} · {Formatting.Count(overview.Artifacts.Sum(a => a.EntryCount), "file", "files")} · {Formatting.Count(overview.InventoryCount, "inventory record", "inventory records")}"));
        Details.Add(new("Uncompressed size", Formatting.Bytes(manifest.Totals.UncompressedBytes)));
        Details.Add(new("Backup file size", Formatting.Bytes(overview.ArchiveBytes)));
        Details.Add(new("Integrity", "Every index matches the hashes recorded in the manifest."));

        CaptureWarnings.Clear();
        foreach (var warning in manifest.CaptureWarnings)
        {
            CaptureWarnings.Add(warning);
        }

        Artifacts.Clear();
        foreach (var artifact in overview.Artifacts)
        {
            Artifacts.Add(new BackupArtifactRow(artifact));
        }

        Items.Clear();
        foreach (var artifact in overview.Artifacts)
        {
            Items.Add(new RestoreItemRow(artifact, OnSelectionChanged));
        }

        RunPreflightCommand.NotifyCanExecuteChanged();

        SelectedArtifact = Artifacts.FirstOrDefault(a => a.Summary.EntryCount > 0);
        Stage = RestoreStage.Overview;
    }

    private void CloseOverview()
    {
        if (_overview is not null)
        {
            BackupReader.Close(_overview);
            _overview = null;
        }

        Details.Clear();
        CaptureWarnings.Clear();
        Artifacts.Clear();
        Entries.Clear();
        Items.Clear();
        MappingRows.Clear();
        Findings.Clear();
        Groups.Clear();
        Rewrites.Clear();
        Reinstall.Clear();
        McpServers.Clear();
        _userMappings.Clear();
        _decisions.Clear();
        _approval = null;
        ApprovalStatus = null;
        Preflight = null;
        PreflightSummary = null;
        RunPreflightCommand.NotifyCanExecuteChanged();
    }

    private void Fail(string title, string message)
    {
        ErrorTitle = title;
        ErrorMessage = message;
        Stage = RestoreStage.Failed;
    }

    private static (string Title, string Message) Describe(ArchiveException ex) => ex.Kind switch
    {
        ArchiveErrorKind.NotFound => ("The file is missing", "The selected file no longer exists. It may have been moved or deleted."),
        ArchiveErrorKind.NotADevbrArchive => ("This is not a DevBR backup", "The file is not a 7z-based .devbr archive."),
        ArchiveErrorKind.WrongPasswordOrCorrupt or ArchiveErrorKind.Corrupt => ("The backup appears damaged", "DevBR could not read the archive. Copy the file again from its source and retry."),
        ArchiveErrorKind.UnsafeEntryPath => ("The backup contains unsafe paths", $"{ex.Message} DevBR will not restore from this file."),
        ArchiveErrorKind.LimitExceeded => ("The backup exceeds safety limits", ex.Message),
        ArchiveErrorKind.WorkerUnavailable => ("The archive process stopped", "The background process that reads backups stopped unexpectedly. Try opening the file again."),
        _ => ("The backup could not be opened", ex.Message),
    };
}
