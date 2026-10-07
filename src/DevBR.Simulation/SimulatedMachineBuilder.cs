using System.Text;
using System.Text.Json;
using DevBR.Application.Machine;

namespace DevBR.Simulation;

/// <summary>Writes a simulated machine fixture to disk.</summary>
public sealed class SimulatedMachineBuilder
{
    private readonly string _userName;
    private readonly string _computerName;
    private readonly List<(string Path, byte[]? Content)> _entries = [];
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, RegistryValueDefinition>>> _registry = new(StringComparer.OrdinalIgnoreCase);
    private readonly MachineDefinition _definition;

    public SimulatedMachineBuilder(string userName = "alice", string computerName = "SIM-WORKSTATION", bool elevated = false)
    {
        _userName = userName;
        _computerName = computerName;
        var profile = $@"C:\Users\{userName}";
        Folders = new MachineFolders(
            SystemDrive: @"C:\",
            Windows: @"C:\Windows",
            UsersRoot: @"C:\Users",
            UserProfile: profile,
            RoamingAppData: $@"{profile}\AppData\Roaming",
            LocalAppData: $@"{profile}\AppData\Local",
            Documents: $@"{profile}\Documents",
            ProgramData: @"C:\ProgramData",
            ProgramFiles: @"C:\Program Files",
            ProgramFilesX86: @"C:\Program Files (x86)",
            StartMenuUser: $@"{profile}\AppData\Roaming\Microsoft\Windows\Start Menu\Programs",
            StartMenuCommon: @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs");

        _definition = new MachineDefinition
        {
            Info = new MachineInfo(computerName, userName, "Windows 11 Pro", "24H2", "26100", "X64", elevated),
            Folders = Folders,
            Drives = [new DriveRecord(@"C:\", "System", "NTFS", 512L << 30, 200L << 30)],
        };

        foreach (var folder in new[] { Folders.Windows, Folders.ProgramFiles, Folders.ProgramFilesX86, Folders.ProgramData, Folders.Documents, Folders.LocalAppData, Folders.RoamingAppData, Folders.StartMenuUser, Folders.StartMenuCommon })
        {
            Directory(folder);
        }

        Environment(RegistryHive.CurrentUser, "TEMP", @"%USERPROFILE%\AppData\Local\Temp", expand: true);
        Environment(RegistryHive.LocalMachine, "ComSpec", @"%SystemRoot%\system32\cmd.exe", expand: true);
        Environment(RegistryHive.LocalMachine, "Path", @"%SystemRoot%\system32;%SystemRoot%", expand: true);
        Environment(RegistryHive.LocalMachine, "OS", "Windows_NT");
        Environment(RegistryHive.LocalMachine, "PROCESSOR_ARCHITECTURE", "AMD64");
    }

    public MachineFolders Folders { get; }

    public string UserName => _userName;

    public string ComputerName => _computerName;

    public SimulatedMachineBuilder Drive(string root, string label, long totalBytes = 1L << 40)
    {
        _definition.Drives.Add(new DriveRecord(root, label, "NTFS", totalBytes, totalBytes / 2));
        return this;
    }

    public SimulatedMachineBuilder Directory(string path)
    {
        _entries.Add((path, null));
        return this;
    }

    public SimulatedMachineBuilder File(string path, string content) => File(path, Encoding.UTF8.GetBytes(content));

    public SimulatedMachineBuilder File(string path, byte[] content)
    {
        _entries.Add((path, content));
        return this;
    }

    /// <summary>An executable stand-in. Its version is served from metadata, as a PE version resource would be.</summary>
    public SimulatedMachineBuilder Executable(string path, string? version = null)
    {
        File(path, "MZ simulated executable"u8.ToArray());
        if (version is not null)
        {
            _definition.FileVersions[path] = version;
        }

        return this;
    }

    public SimulatedMachineBuilder Junction(string path, string target)
    {
        Directory(path);
        _definition.Junctions[path] = target;
        return this;
    }

    public SimulatedMachineBuilder Inaccessible(string path)
    {
        Directory(path);
        _definition.Inaccessible.Add(path);
        return this;
    }

    /// <summary>A file another program holds open exclusively (e.g. a running app's database).</summary>
    public SimulatedMachineBuilder LockedFile(string path, string content = "locked")
    {
        File(path, content);
        _definition.Locked.Add(path);
        return this;
    }

    public SimulatedMachineBuilder Placeholder(string path, string content = "cloud-only")
    {
        File(path, content);
        _definition.Placeholders.Add(path);
        return this;
    }

    public SimulatedMachineBuilder StorePackage(string name, string publisherId, string displayName, string version, string publisher = "CN=Microsoft Corporation")
    {
        var family = $"{name}_{publisherId}";
        var full = $"{name}_{version}_x64__{publisherId}";
        var location = $@"C:\Program Files\WindowsApps\{full}";
        Directory(location);
        _definition.StorePackages.Add(new Application.Machine.StorePackage(family, full, name, displayName, publisher, version, location, false));
        return this;
    }

    public SimulatedMachineBuilder RegistryValue(RegistryHive hive, RegistryView view, string key, string name, RegistryValueKind kind, object data)
    {
        var hiveName = SimulatedRegistry.HiveName(hive, view);
        if (!_registry.TryGetValue(hiveName, out var keys))
        {
            _registry[hiveName] = keys = new(StringComparer.OrdinalIgnoreCase);
        }

        if (!keys.TryGetValue(key, out var values))
        {
            keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
        }

        var element = data is byte[] bytes ? JsonSerializer.SerializeToElement(Convert.ToBase64String(bytes)) : JsonSerializer.SerializeToElement(data);
        values[name] = new RegistryValueDefinition(kind, element);
        return this;
    }

    public SimulatedMachineBuilder RegistryKey(RegistryHive hive, RegistryView view, string key, params (string Name, object Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            var kind = value switch
            {
                int => RegistryValueKind.DWord,
                long => RegistryValueKind.QWord,
                string[] => RegistryValueKind.MultiString,
                byte[] => RegistryValueKind.Binary,
                _ => RegistryValueKind.String,
            };
            RegistryValue(hive, view, key, name, kind, value);
        }

        if (values.Length == 0)
        {
            RegistryValue(hive, view, key, string.Empty, RegistryValueKind.String, string.Empty);
        }

        return this;
    }

    public SimulatedMachineBuilder InaccessibleRegistryKey(RegistryHive hive, RegistryView view, string key)
        => RegistryValue(hive, view, key, "__accessDenied", RegistryValueKind.DWord, 1);

    public SimulatedMachineBuilder Environment(RegistryHive hive, string name, string value, bool expand = false)
        => RegistryValue(hive, RegistryView.Registry64,
            hive == RegistryHive.CurrentUser ? "Environment" : @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
            name, expand ? RegistryValueKind.ExpandString : RegistryValueKind.String, value);

    /// <summary>An Add/Remove Programs record.</summary>
    public SimulatedMachineBuilder UninstallEntry(RegistryHive hive, RegistryView view, string keyName, string displayName, string? version, string? publisher,
        string? installLocation = null, string? displayIcon = null, bool systemComponent = false)
    {
        var key = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{keyName}";
        RegistryValue(hive, view, key, "DisplayName", RegistryValueKind.String, displayName);
        if (version is not null) { RegistryValue(hive, view, key, "DisplayVersion", RegistryValueKind.String, version); }
        if (publisher is not null) { RegistryValue(hive, view, key, "Publisher", RegistryValueKind.String, publisher); }
        if (installLocation is not null) { RegistryValue(hive, view, key, "InstallLocation", RegistryValueKind.String, installLocation); }
        if (displayIcon is not null) { RegistryValue(hive, view, key, "DisplayIcon", RegistryValueKind.String, displayIcon); }
        if (systemComponent) { RegistryValue(hive, view, key, "SystemComponent", RegistryValueKind.DWord, 1); }
        RegistryValue(hive, view, key, "UninstallString", RegistryValueKind.String, $"\"{installLocation ?? @"C:\Program Files\Unknown"}\\unins000.exe\"");
        return this;
    }

    public SimulatedMachineBuilder AppPath(RegistryHive hive, string exeName, string path)
        => RegistryValue(hive, RegistryView.Registry64, $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}", string.Empty, RegistryValueKind.String, path);

    public SimulatedMachineBuilder Shortcut(string linkPath, string targetPath) => File(linkPath, ShellLinkWriter.Create(targetPath));

    /// <summary>A Git working tree with HEAD, config and one object, enough for detection.</summary>
    public SimulatedMachineBuilder GitRepository(string path, string branch = "main", string? remoteUrl = null, bool lfs = false, bool locked = false, IReadOnlyList<string>? tracked = null)
    {
        File($@"{path}\.git\index", GitIndexWriter.Create(tracked ?? ["README.md"]));
        var git = $@"{path}\.git";
        File($@"{git}\HEAD", $"ref: refs/heads/{branch}\n");
        var config = "[core]\n\trepositoryformatversion = 0\n\tbare = false\n";
        if (remoteUrl is not null)
        {
            config += $"[remote \"origin\"]\n\turl = {remoteUrl}\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n";
        }

        File($@"{git}\config", config);
        File($@"{git}\refs\heads\{branch}", "0123456789abcdef0123456789abcdef01234567\n");
        Directory($@"{git}\objects\pack");
        if (lfs) { Directory($@"{git}\lfs\objects"); }
        if (locked) { File($@"{git}\index.lock", string.Empty); }
        File($@"{path}\README.md", "# project\n");
        return this;
    }

    public SimulatedMachine Build(string root)
    {
        root = Path.GetFullPath(root);
        if (System.IO.Directory.Exists(root))
        {
            System.IO.Directory.Delete(root, recursive: true);
        }

        System.IO.Directory.CreateDirectory(root);
        foreach (var drive in _definition.Drives)
        {
            System.IO.Directory.CreateDirectory(SimulatedFileSystem.Map(root, drive.Root));
        }

        foreach (var (path, content) in _entries)
        {
            var backing = SimulatedFileSystem.Map(root, path);
            if (content is null)
            {
                System.IO.Directory.CreateDirectory(backing);
            }
            else
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(backing)!);
                System.IO.File.WriteAllBytes(backing, content);
            }
        }

        System.IO.File.WriteAllText(Path.Combine(root, "machine.json"), JsonSerializer.Serialize(_definition, SimulatedMachine.JsonOptions));
        System.IO.File.WriteAllText(Path.Combine(root, "registry.json"), JsonSerializer.Serialize(_registry, SimulatedMachine.JsonOptions));
        return SimulatedMachine.Load(root);
    }
}

/// <summary>Writes minimal MS-SHLLINK files whose LinkInfo carries a local target path.</summary>
public static class ShellLinkWriter
{
    public static byte[] Create(string targetPath)
    {
        var target = Encoding.Default.GetBytes(targetPath + "\0");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // ShellLinkHeader (76 bytes)
        writer.Write(0x4C);
        writer.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        writer.Write(0x2); // LinkFlags: HasLinkInfo
        writer.Write(0x20); // FileAttributes: archive
        writer.Write(new byte[24]); // times
        writer.Write(0); // file size
        writer.Write(0); // icon index
        writer.Write(1); // show command
        writer.Write((ushort)0); // hotkey
        writer.Write(new byte[10]); // reserved

        // LinkInfo with VolumeIDAndLocalBasePath
        const int headerSize = 0x1C;
        const int volumeIdSize = 0x11;
        var linkInfoSize = headerSize + volumeIdSize + target.Length + 1;
        writer.Write(linkInfoSize);
        writer.Write(headerSize);
        writer.Write(0x1); // VolumeIDAndLocalBasePath
        writer.Write(headerSize); // VolumeIDOffset
        writer.Write(headerSize + volumeIdSize); // LocalBasePathOffset
        writer.Write(0); // CommonNetworkRelativeLinkOffset
        writer.Write(headerSize + volumeIdSize + target.Length); // CommonPathSuffixOffset

        writer.Write(volumeIdSize);
        writer.Write(3); // DRIVE_FIXED
        writer.Write(0); // serial
        writer.Write(0x10); // label offset
        writer.Write((byte)0); // empty label
        writer.Write(target);
        writer.Write((byte)0); // empty suffix
        return stream.ToArray();
    }
}
