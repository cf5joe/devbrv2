using System.Diagnostics;
using System.IO;
using DevBR.Domain;
using Microsoft.Win32;

namespace DevBR.App.Services;

public interface IDialogService
{
    string? PickBackupFile();

    string? PickFolder(string title, string? initialDirectory);

    IReadOnlyList<string> PickFiles(string title);

    string? PickSaveBackupFile(string suggestedPath);

    void RevealFolder(string path);

    void OpenDocument(string path);

    /// <summary>A yes/no question; safe to call from any thread.</summary>
    bool Confirm(string title, string message);
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message)
        => System.Windows.Application.Current.Dispatcher.Invoke(() =>
            System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow!, message, title, System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No) == System.Windows.MessageBoxResult.Yes);

    public string? PickBackupFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a DevBR backup",
            Filter = $"DevBR backups (*{ArchiveContract.FileExtension})|*{ArchiveContract.FileExtension}|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public IReadOnlyList<string> PickFiles(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Multiselect = true, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? PickSaveBackupFile(string suggestedPath)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the backup as",
            Filter = $"DevBR backups (*{ArchiveContract.FileExtension})|*{ArchiveContract.FileExtension}",
            DefaultExt = ArchiveContract.FileExtension,
            AddExtension = true,
            FileName = Path.GetFileName(suggestedPath),
            InitialDirectory = Path.GetDirectoryName(suggestedPath),
            // Replacing an existing file is confirmed separately, in the wizard, as an explicit decision.
            OverwritePrompt = false,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void RevealFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false })?.Dispose();
    }

    public void OpenDocument(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false })?.Dispose();
        }
    }
}
