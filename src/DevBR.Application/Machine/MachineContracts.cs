namespace DevBR.Application.Machine;

/// <summary>
/// Everything discovery (and later restore) knows about a computer. The real implementation reads this
/// Windows installation; the simulated one reads a fixture folder, so whole-machine scenarios — other
/// users, both registry views, Store apps, junctions, inaccessible folders, a second "target" computer —
/// can be exercised without a second PC.
/// </summary>
public interface IMachine
{
    MachineInfo Info { get; }

    MachineFolders Folders { get; }

    IRegistry Registry { get; }

    IMachineFileSystem FileSystem { get; }

    IStorePackageSource StorePackages { get; }

    /// <summary>True for a fixture-backed machine. Shown prominently in the UI.</summary>
    bool IsSimulated { get; }

    /// <summary>Executable names of running processes (e.g. "Code.exe"), used to warn before files in use are changed.</summary>
    IReadOnlySet<string> GetRunningProcessNames();
}

/// <param name="ComputerName">Display only; never used as an identity.</param>
public sealed record MachineInfo(
    string ComputerName,
    string UserName,
    string OsProductName,
    string OsDisplayVersion,
    string OsBuild,
    string Architecture,
    bool IsElevated);

/// <summary>Resolved known folders of the current user and machine, honoring folder redirection.</summary>
public sealed record MachineFolders(
    string SystemDrive,
    string Windows,
    string UsersRoot,
    string UserProfile,
    string RoamingAppData,
    string LocalAppData,
    string Documents,
    string ProgramData,
    string ProgramFiles,
    string ProgramFilesX86,
    string StartMenuUser,
    string StartMenuCommon);

public enum RegistryHive
{
    LocalMachine,
    CurrentUser,
}

public enum RegistryView
{
    Registry64,
    Registry32,
}

public enum RegistryValueKind
{
    String,
    ExpandString,
    MultiString,
    DWord,
    QWord,
    Binary,
    Unknown,
}

/// <summary>A raw registry value. <see cref="ExpandString"/> values are never expanded here.</summary>
public sealed record RegistryValue(RegistryValueKind Kind, object Data)
{
    public string? AsString() => Data switch
    {
        string s => s,
        string[] lines => string.Join('\n', lines),
        int or long or uint or ulong => Convert.ToString(Data, System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    public long? AsInt64() => Data switch
    {
        int i => i,
        long l => l,
        uint u => u,
        string s when long.TryParse(s, out var parsed) => parsed,
        _ => null,
    };
}

public interface IRegistryKey : IDisposable
{
    /// <summary>Path relative to the hive, e.g. <c>SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo</c>.</summary>
    string Path { get; }

    IReadOnlyList<string> GetSubKeyNames();

    IReadOnlyList<string> GetValueNames();

    RegistryValue? GetValue(string name);

    IRegistryKey? OpenSubKey(string name);
}

public interface IRegistry
{
    /// <returns>Null when the key does not exist. Throws <see cref="UnauthorizedAccessException"/> when it cannot be read.</returns>
    IRegistryKey? OpenKey(RegistryHive hive, RegistryView view, string path);
}

[Flags]
public enum EntryAttributes
{
    None = 0,
    Directory = 1,
    ReparsePoint = 2,
    Hidden = 4,
    System = 8,
    /// <summary>Cloud placeholder (OneDrive etc.): reading it would download it.</summary>
    CloudPlaceholder = 16,
    Offline = 32,
}

public sealed record FileSystemEntry(string Name, string FullPath, EntryAttributes Attributes, long Length, DateTimeOffset LastWriteUtc)
{
    public bool IsDirectory => Attributes.HasFlag(EntryAttributes.Directory);

    public bool IsReparsePoint => Attributes.HasFlag(EntryAttributes.ReparsePoint);

    public bool IsCloudPlaceholder => Attributes.HasFlag(EntryAttributes.CloudPlaceholder) || Attributes.HasFlag(EntryAttributes.Offline);
}

public sealed record DriveRecord(string Root, string? Label, string? Format, long TotalBytes, long FreeBytes);

/// <summary>
/// Read-only filesystem access for discovery. Implementations never follow junctions or symbolic links
/// on their own, never hydrate cloud placeholders, and never execute anything.
/// </summary>
public interface IMachineFileSystem
{
    IReadOnlyList<DriveRecord> GetFixedDrives();

    /// <summary>Immediate children only. Throws <see cref="UnauthorizedAccessException"/> or <see cref="DirectoryNotFoundException"/>.</summary>
    IEnumerable<FileSystemEntry> EnumerateEntries(string directory);

    FileSystemEntry? GetEntry(string path);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>Reads a small text file; returns null when missing, a placeholder, or larger than <paramref name="maxBytes"/>.</summary>
    string? ReadText(string path, long maxBytes);

    /// <summary>Reads only the start of a file (e.g. a .lnk header) without hydrating placeholders.</summary>
    byte[]? ReadPrefix(string path, int maxBytes);

    /// <summary>Product version from the file's version resource. The file is read, never run.</summary>
    string? GetFileVersion(string path);

    /// <summary>
    /// Opens a file for sequential reading during capture, sharing it with other writers so open
    /// configuration files can still be read. Throws <see cref="IOException"/> when another process
    /// holds it exclusively, and <see cref="UnauthorizedAccessException"/> when access is denied.
    /// </summary>
    Stream OpenRead(string path);
}

public sealed record StorePackage(
    string FamilyName,
    string FullName,
    string Name,
    string DisplayName,
    string Publisher,
    string Version,
    string? InstallLocation,
    bool IsDevelopmentMode);

public interface IStorePackageSource
{
    /// <summary>Packages registered for the current user, excluding frameworks, resource packages and OS components.</summary>
    IReadOnlyList<StorePackage> GetPackagesForCurrentUser();
}

public static class MachineExtensions
{
    private const string UserEnvironmentKey = "Environment";
    private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>
    /// The persisted value of an environment variable (user scope first, then machine scope), expanded
    /// against the persisted environment. The DevBR process environment is deliberately not consulted,
    /// so overrides such as CODEX_HOME reflect what the user configured, not how DevBR was launched.
    /// </summary>
    public static string? GetEnvironmentVariable(this IMachine machine, string name)
    {
        var value = ReadVariable(machine, RegistryHive.CurrentUser, UserEnvironmentKey, name)
            ?? ReadVariable(machine, RegistryHive.LocalMachine, MachineEnvironmentKey, name);
        return value is null ? null : machine.Expand(value.AsString() ?? string.Empty);
    }

    public static IReadOnlyDictionary<string, RegistryValue> ReadEnvironment(this IMachine machine, RegistryHive hive)
    {
        var result = new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
        using var key = machine.Registry.OpenKey(hive, RegistryView.Registry64, hive == RegistryHive.CurrentUser ? UserEnvironmentKey : MachineEnvironmentKey);
        if (key is null)
        {
            return result;
        }

        foreach (var name in key.GetValueNames())
        {
            if (name.Length > 0 && key.GetValue(name) is { } value)
            {
                result[name] = value;
            }
        }

        return result;
    }

    /// <summary>Expands %VARIABLES% using the machine's persisted environment and known folders.</summary>
    public static string Expand(this IMachine machine, string value)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder();
        var index = 0;
        while (index < value.Length)
        {
            var start = value.IndexOf('%', index);
            var end = start < 0 ? -1 : value.IndexOf('%', start + 1);
            if (start < 0 || end < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            builder.Append(value, index, start - index);
            var name = value.Substring(start + 1, end - start - 1);
            var replacement = WellKnownVariable(machine, name)
                ?? ReadVariable(machine, RegistryHive.CurrentUser, UserEnvironmentKey, name)?.AsString()
                ?? ReadVariable(machine, RegistryHive.LocalMachine, MachineEnvironmentKey, name)?.AsString();

            if (replacement is null || replacement.Contains('%' + name + '%', StringComparison.OrdinalIgnoreCase))
            {
                builder.Append(value, start, end - start + 1);
            }
            else
            {
                builder.Append(replacement);
            }

            index = end + 1;
        }

        return builder.ToString();
    }

    private static string? WellKnownVariable(IMachine machine, string name) => name.ToUpperInvariant() switch
    {
        "USERPROFILE" => machine.Folders.UserProfile,
        "APPDATA" => machine.Folders.RoamingAppData,
        "LOCALAPPDATA" => machine.Folders.LocalAppData,
        "PROGRAMDATA" or "ALLUSERSPROFILE" => machine.Folders.ProgramData,
        "PROGRAMFILES" => machine.Folders.ProgramFiles,
        "PROGRAMFILES(X86)" => machine.Folders.ProgramFilesX86,
        "SYSTEMROOT" or "WINDIR" => machine.Folders.Windows,
        "SYSTEMDRIVE" => machine.Folders.SystemDrive.TrimEnd('\\'),
        "USERNAME" => machine.Info.UserName,
        "HOMEDRIVE" => System.IO.Path.GetPathRoot(machine.Folders.UserProfile)?.TrimEnd('\\'),
        "HOMEPATH" => machine.Folders.UserProfile[(System.IO.Path.GetPathRoot(machine.Folders.UserProfile)?.Length ?? 0)..].Insert(0, "\\"),
        _ => null,
    };

    private static RegistryValue? ReadVariable(IMachine machine, RegistryHive hive, string keyPath, string name)
    {
        try
        {
            using var key = machine.Registry.OpenKey(hive, RegistryView.Registry64, keyPath);
            return key?.GetValue(name);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
