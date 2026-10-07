using System.Runtime.InteropServices;
using DevBR.Application.Machine;
using Win32 = Microsoft.Win32;

namespace DevBR.Infrastructure.Machine;

/// <summary>Writes to this computer for an approved restore. Machine-wide changes are not possible here; they go through the broker.</summary>
public sealed partial class WindowsMachineWriter : IMachineWriter
{
    private const int HwndBroadcast = 0xFFFF;
    private const int WmSettingChange = 0x001A;
    private const int SmtoAbortIfHung = 0x0002;

    public void WriteFileAtomic(string path, byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        WriteFileAtomic(path, stream);
    }

    public void WriteFileAtomic(string path, Stream content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new IOException($"{path} has no folder.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.devbr-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                content.CopyTo(output);
            }

            // Same volume: a rename that replaces the destination in one step.
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeleteDirectoryTree(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    public void SetUserEnvironmentVariable(string name, string? value, RegistryValueKind kind)
    {
        using var key = Win32.Registry.CurrentUser.CreateSubKey("Environment", writable: true);
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value, kind == RegistryValueKind.ExpandString ? Win32.RegistryValueKind.ExpandString : Win32.RegistryValueKind.String);
        }
    }

    public void BroadcastEnvironmentChange()
        => _ = SendMessageTimeoutW(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 5000, out _);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeoutW(nint hWnd, int msg, nint wParam, string lParam, int flags, int timeout, out nint result);
}
