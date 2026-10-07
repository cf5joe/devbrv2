using System.Diagnostics;
using System.Runtime.InteropServices;
using DevBR.Application.Machine;
using Microsoft.Win32;
using RegistryHive = DevBR.Application.Machine.RegistryHive;
using RegistryValueKind = DevBR.Application.Machine.RegistryValueKind;
using RegistryView = DevBR.Application.Machine.RegistryView;
using Win32 = Microsoft.Win32;

namespace DevBR.Infrastructure.Machine;

/// <summary>This Windows installation, read-only.</summary>
public sealed class WindowsMachine : IMachine
{
    public WindowsMachine()
    {
        Folders = new MachineFolders(
            SystemDrive: Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\",
            Windows: Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            UsersRoot: Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users",
            UserProfile: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RoamingAppData: Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            LocalAppData: Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Documents: Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            ProgramData: Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ProgramFiles: Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ProgramFilesX86: Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            StartMenuUser: Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            StartMenuCommon: Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));

        using var version = Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = version?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var productName = version?.GetValue("ProductName") as string ?? "Windows";

        // ProductName still says "Windows 10" on Windows 11; the build number is authoritative.
        if (int.TryParse(build, out var buildNumber) && buildNumber >= 22000)
        {
            productName = productName.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        }

        Info = new MachineInfo(
            Environment.MachineName,
            Environment.UserName,
            productName,
            version?.GetValue("DisplayVersion") as string ?? string.Empty,
            build,
            RuntimeInformation.OSArchitecture.ToString(),
            BuildInfo.IsElevated);
    }

    public MachineInfo Info { get; }

    public MachineFolders Folders { get; }

    public IRegistry Registry { get; } = new WindowsRegistry();

    public IMachineFileSystem FileSystem { get; } = new WindowsFileSystem();

    public IStorePackageSource StorePackages { get; } = new WindowsStorePackageSource();

    public bool IsSimulated => false;
}

internal sealed class WindowsRegistry : IRegistry
{
    public IRegistryKey? OpenKey(RegistryHive hive, RegistryView view, string path)
    {
        var baseKey = Win32.RegistryKey.OpenBaseKey(
            hive == RegistryHive.LocalMachine ? Win32.RegistryHive.LocalMachine : Win32.RegistryHive.CurrentUser,
            view == RegistryView.Registry64 ? Win32.RegistryView.Registry64 : Win32.RegistryView.Registry32);

        try
        {
            var key = baseKey.OpenSubKey(path, writable: false);
            return key is null ? null : new WindowsRegistryKey(key, path);
        }
        catch (System.Security.SecurityException ex)
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
        finally
        {
            baseKey.Dispose();
        }
    }
}

internal sealed class WindowsRegistryKey(Win32.RegistryKey key, string path) : IRegistryKey
{
    public string Path { get; } = path;

    public IReadOnlyList<string> GetSubKeyNames() => key.GetSubKeyNames();

    public IReadOnlyList<string> GetValueNames() => key.GetValueNames();

    public RegistryValue? GetValue(string name)
    {
        var data = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (data is null)
        {
            return null;
        }

        var kind = key.GetValueKind(name) switch
        {
            Win32.RegistryValueKind.String => RegistryValueKind.String,
            Win32.RegistryValueKind.ExpandString => RegistryValueKind.ExpandString,
            Win32.RegistryValueKind.MultiString => RegistryValueKind.MultiString,
            Win32.RegistryValueKind.DWord => RegistryValueKind.DWord,
            Win32.RegistryValueKind.QWord => RegistryValueKind.QWord,
            Win32.RegistryValueKind.Binary => RegistryValueKind.Binary,
            _ => RegistryValueKind.Unknown,
        };

        return new RegistryValue(kind, data);
    }

    public IRegistryKey? OpenSubKey(string name)
    {
        try
        {
            var child = key.OpenSubKey(name, writable: false);
            return child is null ? null : new WindowsRegistryKey(child, $@"{Path}\{name}");
        }
        catch (System.Security.SecurityException ex)
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
    }

    public void Dispose() => key.Dispose();
}

internal sealed class WindowsFileSystem : IMachineFileSystem
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private static readonly EnumerationOptions ChildrenOnly = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    public IReadOnlyList<DriveRecord> GetFixedDrives()
    {
        var drives = new List<DriveRecord>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            try
            {
                drives.Add(drive.IsReady
                    ? new DriveRecord(drive.RootDirectory.FullName, drive.VolumeLabel, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace)
                    : new DriveRecord(drive.RootDirectory.FullName, null, null, 0, 0));
            }
            catch (IOException)
            {
                drives.Add(new DriveRecord(drive.RootDirectory.FullName, null, null, 0, 0));
            }
        }

        return drives;
    }

    public IEnumerable<FileSystemEntry> EnumerateEntries(string directory)
    {
        // Materialized so access errors surface here, not halfway through the caller's loop.
        return [.. new DirectoryInfo(directory).EnumerateFileSystemInfos("*", ChildrenOnly).Select(ToEntry)];
    }

    public FileSystemEntry? GetEntry(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists ? ToEntry(info) : null;
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? ReadText(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes || IsPlaceholder(info.Attributes))
            {
                return null;
            }

            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public byte[]? ReadPrefix(string path, int maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || IsPlaceholder(info.Attributes))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[Math.Min(maxBytes, (int)Math.Min(int.MaxValue, info.Length))];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return buffer[..read];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? GetFileVersion(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || IsPlaceholder(info.Attributes))
            {
                return null;
            }

            // Reads the version resource only; the file is not loaded for execution.
            var version = FileVersionInfo.GetVersionInfo(path);
            var text = version.ProductVersion ?? version.FileVersion;
            return string.IsNullOrWhiteSpace(text) ? null : text.Split(['+', ' '], 2)[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    public Stream OpenRead(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    private static bool IsPlaceholder(FileAttributes attributes)
        => (attributes & (RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline)) != 0;

    private static FileSystemEntry ToEntry(FileSystemInfo info)
    {
        var a = info.Attributes;
        var attributes = EntryAttributes.None;
        if (a.HasFlag(FileAttributes.Directory)) { attributes |= EntryAttributes.Directory; }
        if (a.HasFlag(FileAttributes.ReparsePoint)) { attributes |= EntryAttributes.ReparsePoint; }
        if (a.HasFlag(FileAttributes.Hidden)) { attributes |= EntryAttributes.Hidden; }
        if (a.HasFlag(FileAttributes.System)) { attributes |= EntryAttributes.System; }
        if ((a & (RecallOnOpen | RecallOnDataAccess)) != 0) { attributes |= EntryAttributes.CloudPlaceholder; }
        if (a.HasFlag(FileAttributes.Offline)) { attributes |= EntryAttributes.Offline; }

        var length = info is FileInfo file ? file.Length : 0;
        return new FileSystemEntry(info.Name, info.FullName, attributes, length, info.LastWriteTimeUtc);
    }
}

internal sealed class WindowsStorePackageSource : IStorePackageSource
{
    public IReadOnlyList<StorePackage> GetPackagesForCurrentUser()
    {
        var manager = new Windows.Management.Deployment.PackageManager();
        var packages = new List<StorePackage>();

        // An empty user SID means "the current user" and needs no elevation.
        foreach (var package in manager.FindPackagesForUser(string.Empty))
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.IsBundle ||
                    package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System)
                {
                    continue;
                }

                var id = package.Id;
                var v = id.Version;
                string? location = null;
                try
                {
                    location = package.InstalledLocation?.Path;
                }
                catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or COMException)
                {
                }

                string displayName;
                try
                {
                    displayName = string.IsNullOrWhiteSpace(package.DisplayName) ? id.Name : package.DisplayName;
                }
                catch (COMException)
                {
                    displayName = id.Name;
                }

                packages.Add(new StorePackage(
                    id.FamilyName, id.FullName, id.Name, displayName, id.Publisher,
                    $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}", location, package.IsDevelopmentMode));
            }
            catch (COMException)
            {
                // A broken registration must not stop the inventory.
            }
        }

        return packages;
    }
}
