using System.Text.Json;
using System.Text.Json.Serialization;
using DevBR.Application.Machine;

namespace DevBR.Simulation;

/// <summary>Serialized description of a simulated computer (machine.json).</summary>
public sealed class MachineDefinition
{
    public required MachineInfo Info { get; init; }

    public required MachineFolders Folders { get; init; }

    public List<DriveRecord> Drives { get; init; } = [];

    /// <summary>Virtual directory path → link target. Reported as reparse points and never entered.</summary>
    public Dictionary<string, string> Junctions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Virtual directories whose listing fails with access denied.</summary>
    public List<string> Inaccessible { get; init; } = [];

    /// <summary>Virtual files that are cloud placeholders (reading them would download them).</summary>
    public List<string> Placeholders { get; init; } = [];

    /// <summary>Executable names of processes "running" on the simulated machine.</summary>
    public List<string> RunningProcesses { get; init; } = [];

    /// <summary>Virtual files held open exclusively by another process (reads fail with a sharing violation).</summary>
    public List<string> Locked { get; init; } = [];

    /// <summary>Virtual file path → version resource, standing in for PE metadata.</summary>
    public Dictionary<string, string> FileVersions { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<StorePackage> StorePackages { get; init; } = [];
}

public sealed record RegistryValueDefinition(RegistryValueKind Kind, JsonElement Data);

/// <summary>
/// A computer described by a fixture folder: <c>machine.json</c>, <c>registry.json</c> and an
/// <c>fs\&lt;drive letter&gt;\…</c> tree holding real files at virtual Windows paths.
/// </summary>
public sealed class SimulatedMachine : IMachine
{
    internal static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private SimulatedMachine(string root, MachineDefinition definition, SimulatedRegistry registry)
    {
        Root = root;
        Definition = definition;
        Registry = registry;
        FileSystem = new SimulatedFileSystem(root, definition);
        StorePackages = new SimulatedStorePackages(definition.StorePackages);
    }

    public string Root { get; }

    public MachineDefinition Definition { get; }

    public MachineInfo Info => Definition.Info;

    public MachineFolders Folders => Definition.Folders;

    public IRegistry Registry { get; }

    public IMachineFileSystem FileSystem { get; }

    public IStorePackageSource StorePackages { get; }

    public bool IsSimulated => true;

    public IReadOnlySet<string> GetRunningProcessNames() => new HashSet<string>(Definition.RunningProcesses, StringComparer.OrdinalIgnoreCase);

    public static bool IsMachineFolder(string root) => File.Exists(Path.Combine(root, "machine.json"));

    public static SimulatedMachine Load(string root)
    {
        root = Path.GetFullPath(root);
        var definition = JsonSerializer.Deserialize<MachineDefinition>(File.ReadAllBytes(Path.Combine(root, "machine.json")), JsonOptions)
            ?? throw new InvalidDataException("machine.json is empty.");

        var registryPath = Path.Combine(root, "registry.json");
        var hives = File.Exists(registryPath)
            ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, RegistryValueDefinition>>>>(File.ReadAllBytes(registryPath), JsonOptions)
            : null;

        return new SimulatedMachine(root, definition, new SimulatedRegistry(hives ?? []));
    }

    /// <summary>Maps a virtual Windows path (C:\Users\…) to its backing file under the fixture.</summary>
    public string MapPath(string virtualPath) => SimulatedFileSystem.Map(Root, virtualPath);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal sealed class SimulatedRegistry : IRegistry
{
    // "HKLM64", "HKLM32", "HKCU" → key path → values. HKCU is shared by both views, as on real Windows.
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, RegistryValueDefinition>>> _hives;

    public SimulatedRegistry(Dictionary<string, Dictionary<string, Dictionary<string, RegistryValueDefinition>>> hives)
    {
        _hives = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, keys) in hives)
        {
            _hives[hive] = new Dictionary<string, Dictionary<string, RegistryValueDefinition>>(keys, StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string HiveName(RegistryHive hive, RegistryView view)
        => hive == RegistryHive.CurrentUser ? "HKCU" : view == RegistryView.Registry64 ? "HKLM64" : "HKLM32";

    private readonly Lock _gate = new();

    /// <summary>Sets (or, with a null definition, removes) a value and persists the registry file.</summary>
    internal void SetValue(string root, string hiveName, string key, string name, RegistryValueDefinition? value)
    {
        lock (_gate)
        {
            if (!_hives.TryGetValue(hiveName, out var keys))
            {
                _hives[hiveName] = keys = new(StringComparer.OrdinalIgnoreCase);
            }

            if (!keys.TryGetValue(key, out var values))
            {
                keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
            }

            if (value is null)
            {
                values.Remove(name);
            }
            else
            {
                values[name] = value;
            }

            File.WriteAllText(Path.Combine(root, "registry.json"), JsonSerializer.Serialize(_hives, SimulatedMachine.JsonOptions));
        }
    }

    public IRegistryKey? OpenKey(RegistryHive hive, RegistryView view, string path)
    {
        if (!_hives.TryGetValue(HiveName(hive, view), out var keys))
        {
            return null;
        }

        path = path.Trim('\\');
        var prefix = path + "\\";
        var exists = keys.ContainsKey(path) || keys.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            return null;
        }

        if (keys.TryGetValue(path, out var values) && values.ContainsKey("__accessDenied"))
        {
            throw new UnauthorizedAccessException($"Access to registry key '{path}' is denied.");
        }

        return new SimulatedRegistryKey(this, hive, view, keys, path);
    }

    private sealed class SimulatedRegistryKey(SimulatedRegistry registry, RegistryHive hive, RegistryView view,
        Dictionary<string, Dictionary<string, RegistryValueDefinition>> keys, string path) : IRegistryKey
    {
        public string Path => path;

        public IReadOnlyList<string> GetSubKeyNames()
        {
            var prefix = path + "\\";
            return [.. keys.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(k => k[prefix.Length..].Split('\\')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        public IReadOnlyList<string> GetValueNames()
            => keys.TryGetValue(path, out var values) ? [.. values.Keys.Where(n => n != "__accessDenied")] : [];

        public RegistryValue? GetValue(string name)
        {
            if (!keys.TryGetValue(path, out var values) || !values.TryGetValue(name, out var definition))
            {
                return null;
            }

            object data = definition.Kind switch
            {
                RegistryValueKind.DWord => definition.Data.GetInt32(),
                RegistryValueKind.QWord => definition.Data.GetInt64(),
                RegistryValueKind.MultiString => definition.Data.Deserialize<string[]>() ?? [],
                RegistryValueKind.Binary => Convert.FromBase64String(definition.Data.GetString() ?? string.Empty),
                _ => definition.Data.GetString() ?? string.Empty,
            };
            return new RegistryValue(definition.Kind, data);
        }

        public IRegistryKey? OpenSubKey(string name) => registry.OpenKey(hive, view, $@"{path}\{name}");

        public void Dispose()
        {
        }
    }
}

internal sealed class SimulatedFileSystem(string root, MachineDefinition definition) : IMachineFileSystem
{
    public static string Map(string root, string virtualPath)
    {
        var full = virtualPath.Replace('/', '\\');
        if (full.Length < 2 || full[1] != ':')
        {
            throw new ArgumentException($"'{virtualPath}' is not an absolute Windows path.", nameof(virtualPath));
        }

        var rest = full[2..].TrimStart('\\');
        return Path.Combine(root, "fs", char.ToUpperInvariant(full[0]).ToString(), rest);
    }

    public IReadOnlyList<DriveRecord> GetFixedDrives() => definition.Drives;

    public IEnumerable<FileSystemEntry> EnumerateEntries(string directory)
    {
        var virtualDirectory = Normalize(directory);
        if (definition.Inaccessible.Any(p => PathEquals(p, virtualDirectory)))
        {
            throw new UnauthorizedAccessException($"Access to the path '{virtualDirectory}' is denied.");
        }

        var backing = Map(root, virtualDirectory);
        if (!Directory.Exists(backing))
        {
            throw new DirectoryNotFoundException($"Could not find a part of the path '{virtualDirectory}'.");
        }

        return [.. new DirectoryInfo(backing).EnumerateFileSystemInfos().Select(info => ToEntry(info, Path.Combine(virtualDirectory, info.Name)))];
    }

    public FileSystemEntry? GetEntry(string path)
    {
        var backing = Map(root, path);
        FileSystemInfo info = Directory.Exists(backing) ? new DirectoryInfo(backing) : new FileInfo(backing);
        return info.Exists ? ToEntry(info, Normalize(path)) : null;
    }

    public bool FileExists(string path) => File.Exists(Map(root, path));

    public bool DirectoryExists(string path) => Directory.Exists(Map(root, path));

    public string? ReadText(string path, long maxBytes)
    {
        var backing = Map(root, path);
        if (IsPlaceholder(path) || !File.Exists(backing) || new FileInfo(backing).Length > maxBytes)
        {
            return null;
        }

        return File.ReadAllText(backing);
    }

    public byte[]? ReadPrefix(string path, int maxBytes)
    {
        var backing = Map(root, path);
        if (IsPlaceholder(path) || !File.Exists(backing))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(backing);
        return bytes.Length <= maxBytes ? bytes : bytes[..maxBytes];
    }

    public string? GetFileVersion(string path)
        => definition.FileVersions.TryGetValue(Normalize(path), out var version) ? version : null;

    public Stream OpenRead(string path)
    {
        if (definition.Locked.Any(p => PathEquals(p, Normalize(path))))
        {
            throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.", unchecked((int)0x80070020));
        }

        return new FileStream(Map(root, path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
    }

    private bool IsPlaceholder(string path) => definition.Placeholders.Any(p => PathEquals(p, Normalize(path)));

    private FileSystemEntry ToEntry(FileSystemInfo info, string virtualPath)
    {
        var attributes = info is DirectoryInfo ? EntryAttributes.Directory : EntryAttributes.None;
        if (definition.Junctions.ContainsKey(virtualPath))
        {
            attributes |= EntryAttributes.ReparsePoint | EntryAttributes.Directory;
        }

        if (IsPlaceholder(virtualPath))
        {
            attributes |= EntryAttributes.CloudPlaceholder;
        }

        if (info.Name.StartsWith('.') || info.Attributes.HasFlag(FileAttributes.Hidden))
        {
            attributes |= EntryAttributes.Hidden;
        }

        return new FileSystemEntry(info.Name, virtualPath, attributes, info is FileInfo file ? file.Length : 0, info.LastWriteTimeUtc);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(path.Replace('/', '\\')) is var trimmed && trimmed.EndsWith(':') ? trimmed + "\\" : Path.TrimEndingDirectorySeparator(path.Replace('/', '\\'));

    private static bool PathEquals(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
}

internal sealed class SimulatedStorePackages(IReadOnlyList<StorePackage> packages) : IStorePackageSource
{
    public IReadOnlyList<StorePackage> GetPackagesForCurrentUser() => packages;
}
