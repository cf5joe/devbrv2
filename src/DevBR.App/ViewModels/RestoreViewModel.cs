using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevBR.App.Services;
using DevBR.Application;
using DevBR.Application.Archive;
using DevBR.Domain;
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

/// <summary>
/// Restore steps 1–3: choose a backup, unlock it if encrypted, and read its overview. Opening a backup
/// only reads its index and manifest in the archive worker; nothing in it is extracted or executed.
/// </summary>
public sealed partial class RestoreViewModel(IArchiveService archive, IDialogService dialogs, ActivityStore activity) : PageViewModel
{
    private CancellationTokenSource? _inspection;

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
        _password = null;
        FilePath = null;
        FileName = null;
        Details.Clear();
        CaptureWarnings.Clear();
        Stage = RestoreStage.ChooseFile;
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
            var inspection = await archive.InspectAsync(
                new ArchiveInspectRequest(FilePath, password, [ArchiveContract.ManifestPath], ArchiveContract.MaxManifestBytes), cts.Token);

            if (!inspection.InlineEntries.TryGetValue(ArchiveContract.ManifestPath, out var manifestJson))
            {
                Fail("This is not a DevBR backup", "The file is a 7z archive, but it has no DevBR manifest. DevBR only restores backups it created.");
                return;
            }

            var manifest = BackupManifestReader.Parse(manifestJson);
            if (manifest.Manifest is null)
            {
                Fail("This backup cannot be read", manifest.Error!);
                return;
            }

            _password = password;
            PasswordError = null;
            ShowOverview(inspection, manifest.Manifest);
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
        finally
        {
            if (ReferenceEquals(_inspection, cts))
            {
                _inspection = null;
            }
        }
    }

    private void ShowOverview(ArchiveInspection inspection, BackupManifest manifest)
    {
        IsEncrypted = inspection.Encrypted;
        Details.Clear();
        Details.Add(new("Source computer", manifest.SourceMachineName));
        Details.Add(new("Source system", $"{manifest.SourceOsDescription} ({manifest.SourceArchitecture})"));
        Details.Add(new("Created", manifest.CreatedAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture)));
        Details.Add(new("Created with", $"DevBR {manifest.AppVersion}"));
        Details.Add(new("Archive format", manifest.FormatVersion.ToString()));
        Details.Add(new("Encryption", inspection.Encrypted ? "AES-256 with encrypted file names" : "Not encrypted"));
        Details.Add(new("Contents", $"{Formatting.Count(manifest.Totals.ArtifactCount, "artifact", "artifacts")} · {Formatting.Count(manifest.Totals.EntryCount, "file", "files")}"));
        Details.Add(new("Uncompressed size", Formatting.Bytes(manifest.Totals.UncompressedBytes)));
        Details.Add(new("Backup file size", Formatting.Bytes(inspection.ArchiveBytes)));

        CaptureWarnings.Clear();
        foreach (var warning in manifest.CaptureWarnings)
        {
            CaptureWarnings.Add(warning);
        }

        Stage = RestoreStage.Overview;
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
        ArchiveErrorKind.WrongPasswordOrCorrupt or ArchiveErrorKind.Corrupt => ("The backup appears damaged", "DevBR could not read the archive index. Copy the file again from its source and retry."),
        ArchiveErrorKind.UnsafeEntryPath => ("The backup contains unsafe paths", $"{ex.Message} DevBR will not restore from this file."),
        ArchiveErrorKind.LimitExceeded => ("The backup exceeds safety limits", ex.Message),
        ArchiveErrorKind.WorkerUnavailable => ("The archive process stopped", "The background process that reads backups stopped unexpectedly. Try opening the file again."),
        _ => ("The backup could not be opened", ex.Message),
    };
}
