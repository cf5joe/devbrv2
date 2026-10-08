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
using DevBR.Infrastructure.Machine;
using DevBR.Infrastructure.Workers;
using DevBR.Application.Machine;
using DevBR.Application.Restore;
using DevBR.Restore.Execution;
using DevBR.Simulation;

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
    RestorePlanner planner, MachineContext machine, RestoreExecutor executor, RestoreRollbackService rollbacks, IRestoreJournal journal,
    BrokerElevationProvider brokerElevation, ProcessPackageInstaller packageInstaller) : PageViewModel
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
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(RestoreNowCommand))]
    public partial RestorePreflight? Preflight { get; set; }

    public bool HasPreflight => Preflight is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunPreflightCommand), nameof(ApproveCommand), nameof(RestoreNowCommand))]
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

    private bool CanRunPreflight() => !IsPreflighting && !IsRestoring && _overview is not null;

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
        ApprovalStatus = $"Plan approved: {count} change{(count == 1 ? string.Empty : "s")} on {machine.Label}. Blocked items are left out until you recheck them.";
        RestoreNowCommand.NotifyCanExecuteChanged();
        await activity.AddAsync(EventSeverity.Information, "Restore", $"Approved a restore plan for {FileName} with {count} changes.", Preflight.Plan.ApprovalHash);
    }

    private bool CanApprove() => Preflight is not null && !IsPreflighting && !IsRestoring && Preflight.Effects.Any();

    // --- Restore execution (steps 8–10: run, verify, report; rollback) -------------------------

    private CancellationTokenSource? _restoreCts;

    public ObservableCollection<RestoreResultRow> Results { get; } = [];

    public ObservableCollection<RecentRestoreRow> RecentRestores { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreNowCommand), nameof(RunPreflightCommand), nameof(ApproveCommand), nameof(RollBackCommand))]
    public partial bool IsRestoring { get; set; }

    [ObservableProperty]
    public partial string? RestoreStatus { get; set; }

    [ObservableProperty]
    public partial double RestorePercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRun))]
    public partial RestoreRun? LastRun { get; set; }

    public bool HasRun => LastRun is not null;

    [ObservableProperty]
    public partial string? RunTitle { get; set; }

    [ObservableProperty]
    public partial string? RunMessage { get; set; }

    [ObservableProperty]
    public partial InfoSeverity RunSeverity { get; set; }

    [ObservableProperty]
    public partial string? RecoveryMessage { get; set; }

    [ObservableProperty]
    public partial string? RollbackMessage { get; set; }

    [ObservableProperty]
    public partial InfoSeverity RollbackSeverity { get; set; }

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task RestoreNowAsync()
    {
        if (Preflight is null || _approval is null || _overview is null)
        {
            return;
        }

        var count = Preflight.Effects.Count();
        if (!dialogs.Confirm("Restore now?",
                $"DevBR will make {count} approved change{(count == 1 ? string.Empty : "s")} on {machine.Label}.\n\n" +
                "It checks this computer again first and stops if anything new would be changed. Files it replaces are kept so you can roll back; installations cannot be rolled back.\n\nContinue?"))
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _restoreCts = cts;
        IsRestoring = true;
        LastRun = null;
        Results.Clear();
        RollbackMessage = null;
        RestorePercent = 0;
        RestoreStatus = "Starting…";
        var approval = _approval;

        try
        {
            var (writer, elevation, installer) = Tools();
            var request = BuildRequest();
            var progress = new Progress<RestoreProgress>(p =>
            {
                RestoreStatus = p.Total == 0 ? p.Message : $"{p.Message} ({p.Done:N0} of {p.Total:N0})";
                RestorePercent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            });
            var run = await Task.Run(() => executor.ExecuteAsync(new RestoreExecutionRequest(request, approval, writer, elevation, installer, paths.ReportsDirectory),
                progress, cts.Token), CancellationToken.None);
            ShowRun(run);
            await activity.AddAsync(run.Outcome == RestoreRunOutcome.Completed ? EventSeverity.Information : EventSeverity.Warning, "Restore",
                $"Restore of {FileName} to {machine.Label}: {run.Message} {run.Count(Domain.RestoreStatus.Applied)} restored, {run.Count(Domain.RestoreStatus.Failed)} failed, {run.Count(Domain.RestoreStatus.Blocked)} blocked.",
                run.JobId.ToString());
            if (run.Outcome == RestoreRunOutcome.ApprovalOutdated)
            {
                _approval = null;
                ApprovalStatus = null;
                ShowPreflight(run.Preflight);
            }
        }
        catch (OperationCanceledException)
        {
            RunSeverity = InfoSeverity.Warning;
            RunTitle = "Restore cancelled";
            RunMessage = "The restore was cancelled before it changed anything.";
        }
        catch (Exception ex) when (ex is ArchiveException or BackupFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RunSeverity = InfoSeverity.Error;
            RunTitle = "The restore stopped";
            RunMessage = $"{ex.Message} Changes made so far are recorded; you can roll them back from Recent restores.";
        }
        finally
        {
            IsRestoring = false;
            RestoreStatus = null;
            _restoreCts = null;
            LoadRecent();
        }
    }

    private bool CanRestore() => _approval is not null && Preflight is not null && !IsPreflighting && !IsRestoring;

    [RelayCommand]
    private void CancelRestore() => _restoreCts?.Cancel();

    [RelayCommand]
    private void OpenReport(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            dialogs.OpenDocument(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRollBack))]
    private async Task RollBackAsync(RecentRestoreRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!string.Equals(row.Summary?.MachineName, machine.Current.Info.ComputerName, StringComparison.OrdinalIgnoreCase))
        {
            RollbackSeverity = InfoSeverity.Warning;
            RollbackMessage = $"That restore was made on {row.Summary?.MachineName ?? "another computer"}. Switch to that computer to roll it back.";
            return;
        }

        if (!dialogs.Confirm("Roll back this restore?",
                "DevBR will undo the file and environment changes this restore made, newest first. Anything changed since the restore is left as it is. Installed software is not removed.\n\nContinue?"))
        {
            return;
        }

        IsRestoring = true;
        RestoreStatus = "Rolling back…";
        try
        {
            var (writer, elevation, _) = Tools();
            var result = await Task.Run(() => rollbacks.RollbackAsync(row.JobId, machine.Current, writer, elevation, CancellationToken.None));
            RollbackSeverity = result.Complete ? InfoSeverity.Success : InfoSeverity.Warning;
            RollbackMessage = (result.Complete
                    ? $"Rolled back {Formatting.Count(result.Undone.Count, "change", "changes")}."
                    : $"Rolled back {Formatting.Count(result.Undone.Count, "change", "changes")}; {Formatting.Count(result.Failed.Count, "change was", "changes were")} kept: " +
                      string.Join(" ", result.Failed.Take(3).Select(f => f.Message)))
                + (result.NotReversible.Count == 0 ? string.Empty : $" {Formatting.Count(result.NotReversible.Count, "installation", "installations")} cannot be rolled back.");
            await activity.AddAsync(result.Complete ? EventSeverity.Information : EventSeverity.Warning, "Restore", $"Rollback of a restore: {RollbackMessage}", row.JobId.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            RollbackSeverity = InfoSeverity.Error;
            RollbackMessage = $"Rollback stopped: {ex.Message}";
        }
        finally
        {
            IsRestoring = false;
            RestoreStatus = null;
            LoadRecent();
        }
    }

    private bool CanRollBack(RecentRestoreRow? row) => !IsRestoring && (row?.CanRollBack ?? true);

    public override Task OnNavigatedToAsync()
    {
        RecoverInterrupted();
        LoadRecent();
        return Task.CompletedTask;
    }

    /// <summary>Settles restores that were interrupted (crash or power loss) on this computer. Nothing is redone.</summary>
    private void RecoverInterrupted()
    {
        foreach (var job in rollbacks.FindInterrupted())
        {
            var summary = RestoreJobSummary.FromJson(job.Summary);
            if (!string.Equals(summary?.MachineName, machine.Current.Info.ComputerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var recovered = rollbacks.Recover(job.JobId, machine.Current);
            var applied = recovered.Count(r => r.Classification == RecoveryClassification.Applied);
            var uncertain = recovered.Count(r => r.Classification == RecoveryClassification.Uncertain);
            RecoveryMessage = $"A restore started {job.CreatedAt.ToLocalTime():g} was interrupted. DevBR checked its unfinished changes: {applied} had been made, " +
                $"{recovered.Count - applied - uncertain} had not, and {uncertain} could not be confirmed. Nothing was redone. Roll it back below, or plan the restore again.";
            _ = activity.AddAsync(EventSeverity.Warning, "Restore", RecoveryMessage, job.JobId.ToString());
        }
    }

    private void LoadRecent()
        => Replace(RecentRestores, journal.ListJobs(JobKinds.Restore, 10).Select(j => new RecentRestoreRow(j, RestoreJobSummary.FromJson(j.Summary))));

    private void ShowRun(RestoreRun run)
    {
        LastRun = run;
        (RunSeverity, RunTitle) = run.Outcome switch
        {
            RestoreRunOutcome.Completed => (InfoSeverity.Success, "Restore finished"),
            RestoreRunOutcome.CompletedWithProblems => (InfoSeverity.Warning, "Restore finished; some items need attention"),
            RestoreRunOutcome.Cancelled => (InfoSeverity.Warning, "Restore cancelled"),
            _ => (InfoSeverity.Warning, "Nothing was restored"),
        };
        RunMessage = run.Outcome == RestoreRunOutcome.ApprovalOutdated
            ? run.Message
            : $"{run.Count(Domain.RestoreStatus.Applied)} restored ({run.Operations.Count(o => o.Verification == VerificationLevel.FunctionallyVerified)} verified working) · " +
              $"{run.Count(Domain.RestoreStatus.Failed)} failed · {run.Count(Domain.RestoreStatus.Blocked)} blocked · {run.Count(Domain.RestoreStatus.Skipped)} skipped. {run.Message}";
        Replace(Results, run.Operations.Select(o => new RestoreResultRow(o)).OrderByDescending(r => r.NeedsAttention).ThenBy(r => r.Item, StringComparer.CurrentCulture));
    }

    private RestoreRequest BuildRequest()
    {
        var work = settings.Current.ScratchDirectory ?? paths.DefaultScratchDirectory;
        Directory.CreateDirectory(work);
        return new RestoreRequest(_overview!, _password, machine.Current, Items.Where(i => i.IsSelected).Select(i => i.Key).ToHashSet(StringComparer.Ordinal),
            [.. _userMappings.Values], new Dictionary<string, ConflictDecision>(_decisions), work);
    }

    /// <summary>How changes reach the current target: this computer (UAC broker, WinGet) or a simulated one.</summary>
    private (IMachineWriter Writer, IElevationProvider Elevation, IPackageInstaller Installer) Tools()
    {
        if (machine.Current is SimulatedMachine simulated)
        {
            var writer = new SimulatedMachineWriter(simulated);
            var elevation = new SimulatedElevationProvider(simulated, writer,
                () => dialogs.Confirm("Simulated administrator prompt", "This simulated computer asks for administrator approval for machine-wide changes, as Windows would.\n\nApprove?"),
                id => RestoreJobSummary.ApprovedEffects(journal, id));
            return (writer, elevation, new SimulatedPackageInstaller(simulated, writer));
        }

        return (new WindowsMachineWriter(), brokerElevation, packageInstaller);
    }

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
            var result = await planner.PreflightAsync(BuildRequest(), new Progress<string>(s => PreflightStatus = s), cts.Token);
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
                RestoreNowCommand.NotifyCanExecuteChanged();
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
        LastRun = null;
        Results.Clear();
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
