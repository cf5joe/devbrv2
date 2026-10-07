using System.Diagnostics;
using System.IO;
using DevBR.Domain;
using Microsoft.Win32;

namespace DevBR.App.Services;

public interface IDialogService
{
    string? PickBackupFile();

    string? PickFolder(string title, string? initialDirectory);

    void RevealFolder(string path);

    void OpenDocument(string path);
}

public sealed class DialogService : IDialogService
{
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
