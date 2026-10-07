using System.Text.Json;
using System.Text.RegularExpressions;
using DevBR.Application.Discovery;
using DevBR.Application.Machine;
using DevBR.Discovery.Catalog;
using DevBR.Discovery.Support;
using DevBR.Domain;

namespace DevBR.Discovery.Providers;

/// <summary>Known tools found in the directories of the persisted user and machine PATH.</summary>
public sealed class PathProvider : IDiscoveryProvider
{
    public string Id => "path";

    public string DisplayName => "PATH directories";

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        var coverage = new CoverageBuilder(DisplayName);
        var items = new List<InventoryItem>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            var scope = hive == RegistryHive.CurrentUser ? "user" : "machine";
            if (!machine.ReadEnvironment(hive).TryGetValue("Path", out var raw))
            {
                continue;
            }

            foreach (var entry in (raw.AsString() ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Paths.Normalize(machine.Expand(entry.Trim('"')));
                if (!visited.Add(directory))
                {
                    continue;
                }

                if (directory.Length < 3 || directory[1] != ':')
                {
                    coverage.Exclude(entry, "PATH entry is not an absolute local path");
                    continue;
                }

                IEnumerable<FileSystemEntry> entries;
                try
                {
                    entries = machine.FileSystem.EnumerateEntries(directory);
                }
                catch (DirectoryNotFoundException)
                {
                    coverage.Exclude(directory, $"PATH ({scope}) entry points to a folder that does not exist");
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    coverage.Inaccessible(directory, "Access denied.");
                    continue;
                }

                coverage.CountDirectory();
                var aliasFolder = Paths.Equal(directory, Paths.Combine(machine.Folders.LocalAppData, "Microsoft", "WindowsApps"));
                foreach (var file in entries.Where(e => !e.IsDirectory))
                {
                    coverage.CountFiles(1);
                    if (KnownTools.ByExecutableName(file.Name, directory) is not { } tool)
                    {
                        continue;
                    }

                    // App execution aliases are stubs for packaged apps (or Store installers), not installations.
                    if (aliasFolder || file.IsReparsePoint)
                    {
                        coverage.Exclude(file.FullPath, "App execution alias (packaged apps are listed separately)");
                        continue;
                    }

                    var item = ItemFactory.Installation(machine, tool, tool.DisplayName, machine.FileSystem.GetFileVersion(file.FullPath), null,
                        ItemFactory.ScopeForPath(machine.Folders, directory), directory,
                        new DiscoveryEvidence($"PATH ({scope})", $"{file.Name} found on PATH", file.FullPath), Confidence.High);
                    items.Add(item);
                    context.OnItemFound?.Invoke(item);
                }
            }
        }

        return Task.FromResult(new DiscoveryResult(items, [coverage.Build()]));
    }
}

/// <summary>Start menu shortcuts (user and all users) that point at known developer tools.</summary>
public sealed class StartMenuProvider : IDiscoveryProvider
{
    public string Id => "start-menu";

    public string DisplayName => "Start menu shortcuts";

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        var coverage = new CoverageBuilder(DisplayName);
        var items = new List<InventoryItem>();

        foreach (var (root, scope) in new[] { (machine.Folders.StartMenuUser, InstallScope.User), (machine.Folders.StartMenuCommon, InstallScope.Machine) })
        {
            Walk(root, 0);

            void Walk(string directory, int depth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IEnumerable<FileSystemEntry> entries;
                try
                {
                    entries = machine.FileSystem.EnumerateEntries(directory);
                }
                catch (DirectoryNotFoundException)
                {
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    coverage.Inaccessible(directory, "Access denied.");
                    return;
                }

                coverage.CountDirectory();
                foreach (var entry in entries)
                {
                    if (entry.IsDirectory)
                    {
                        if (!entry.IsReparsePoint && depth < 4)
                        {
                            Walk(entry.FullPath, depth + 1);
                        }

                        continue;
                    }

                    if (!entry.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    coverage.CountFiles(1);
                    var target = ShellLinkReader.ReadTarget(machine.FileSystem.ReadPrefix(entry.FullPath, 64 * 1024));
                    if (target is null || KnownTools.ByExecutableName(Path.GetFileName(target), Paths.Parent(target)) is not { } tool)
                    {
                        continue;
                    }

                    var item = ItemFactory.Installation(machine, tool, tool.DisplayName, machine.FileSystem.GetFileVersion(target), null,
                        scope == InstallScope.User ? InstallScope.User : ItemFactory.ScopeForPath(machine.Folders, target), Paths.Parent(target),
                        new DiscoveryEvidence("start-menu", $"Shortcut \"{Path.GetFileNameWithoutExtension(entry.Name)}\" → {target}", entry.FullPath), Confidence.Medium);
                    items.Add(item);
                    context.OnItemFound?.Invoke(item);
                }
            }
        }

        return Task.FromResult(new DiscoveryResult(items, [coverage.Build()]));
    }
}

/// <summary>
/// Package-manager metadata read from disk: npm globals, Scoop, Chocolatey, WinGet portable packages,
/// .NET global tools, pipx, uv tools and user-level pip packages. No package manager is executed.
/// </summary>
public sealed partial class PackageManagerProvider : IDiscoveryProvider
{
    public string Id => "package-managers";

    public string DisplayName => "Package managers";

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        var m = context.Machine;
        var f = m.Folders;
        var coverage = new CoverageBuilder(DisplayName);
        var items = new List<InventoryItem>();

        void Add(string manager, string name, string? version, InstallScope scope, string location, KnownTool? tool = null, InventoryCategory? category = null)
        {
            var item = ItemFactory.Installation(m, tool, name, version, null, scope, location,
                new DiscoveryEvidence($"package-manager:{manager}", $"{manager} package {name}{(version is null ? string.Empty : " " + version)}", location),
                Confidence.High, new Dictionary<string, string> { ["packageManager"] = manager, ["package"] = name },
                category ?? (tool is null ? InventoryCategory.Package : null));
            items.Add(item);
            context.OnItemFound?.Invoke(item);
        }

        void Manager(string id, string location, InstallScope scope)
        {
            if (KnownTools.Get(id) is { } tool)
            {
                Add(id, tool.DisplayName, null, scope, location, tool);
            }
        }

        IReadOnlyList<FileSystemEntry> Dirs(string path)
        {
            try
            {
                var entries = m.FileSystem.EnumerateEntries(path).Where(e => e.IsDirectory && !e.IsReparsePoint).ToList();
                coverage.CountDirectory();
                return entries;
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
            catch (UnauthorizedAccessException)
            {
                coverage.Inaccessible(path, "Access denied.");
                return [];
            }
        }

        // npm global packages (prefix override via NPM_CONFIG_PREFIX).
        var npmPrefix = m.GetEnvironmentVariable("NPM_CONFIG_PREFIX") ?? Paths.Combine(f.RoamingAppData, "npm");
        foreach (var entry in Dirs(Paths.Combine(npmPrefix, "node_modules")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packageDirs = entry.Name.StartsWith('@') ? Dirs(entry.FullPath) : [entry];
            foreach (var package in packageDirs)
            {
                using var json = ConfigReaders.ReadJson(m.FileSystem, Paths.Combine(package.FullPath, "package.json"));
                var name = json is null ? package.Name : ConfigReaders.String(json.RootElement, "name") ?? package.Name;
                if (name is "npm" or "corepack")
                {
                    continue;
                }

                Add("npm", name, json is null ? null : ConfigReaders.String(json.RootElement, "version"), InstallScope.User, package.FullPath, KnownTools.ByNpmPackage(name));
            }
        }

        // Scoop (user and global).
        foreach (var (root, scope) in new[]
        {
            (m.GetEnvironmentVariable("SCOOP") ?? Paths.Combine(f.UserProfile, "scoop"), InstallScope.User),
            (m.GetEnvironmentVariable("SCOOP_GLOBAL") ?? Paths.Combine(f.ProgramData, "scoop"), InstallScope.Machine),
        })
        {
            var apps = Dirs(Paths.Combine(root, "apps"));
            if (apps.Count > 0)
            {
                Manager("scoop", root, scope);
            }

            foreach (var app in apps.Where(a => a.Name != "scoop"))
            {
                using var manifest = ConfigReaders.ReadJson(m.FileSystem, Paths.Combine(app.FullPath, "current", "manifest.json"));
                Add("scoop", app.Name, manifest is null ? null : ConfigReaders.String(manifest.RootElement, "version"), scope, app.FullPath);
            }
        }

        // Chocolatey.
        var chocolatey = m.GetEnvironmentVariable("ChocolateyInstall") ?? Paths.Combine(f.ProgramData, "chocolatey");
        var chocoPackages = Dirs(Paths.Combine(chocolatey, "lib"));
        if (chocoPackages.Count > 0)
        {
            Manager("chocolatey", chocolatey, InstallScope.Machine);
        }

        foreach (var package in chocoPackages)
        {
            var nuspec = m.FileSystem.ReadText(Paths.Combine(package.FullPath, package.Name + ".nuspec"), 1024 * 1024);
            var version = nuspec is null ? null : NuspecVersion().Match(nuspec) is { Success: true } match ? match.Groups[1].Value : null;
            Add("chocolatey", package.Name, version, InstallScope.Machine, package.FullPath);
        }

        // WinGet portable packages (user and machine).
        foreach (var (root, scope) in new[] { (Paths.Combine(f.LocalAppData, "Microsoft", "WinGet", "Packages"), InstallScope.User), (Paths.Combine(f.ProgramFiles, "WinGet", "Packages"), InstallScope.Machine) })
        {
            foreach (var package in Dirs(root))
            {
                var name = package.Name;
                var source = name.IndexOf("_Microsoft.Winget.Source", StringComparison.OrdinalIgnoreCase);
                if (source > 0)
                {
                    name = name[..source];
                }

                Add("winget", name, null, scope, package.FullPath);
            }
        }

        // .NET global tools.
        foreach (var tool in Dirs(Paths.Combine(f.UserProfile, ".dotnet", "tools", ".store")))
        {
            var version = Dirs(tool.FullPath).Select(v => v.Name).OrderDescending(StringComparer.Ordinal).FirstOrDefault();
            Add("dotnet-tool", tool.Name, version, InstallScope.User, tool.FullPath);
        }

        // pipx.
        var pipxHome = m.GetEnvironmentVariable("PIPX_HOME");
        foreach (var root in new[] { pipxHome, Paths.Combine(f.UserProfile, "pipx"), Paths.Combine(f.UserProfile, ".local", "pipx") }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var venv in Dirs(Paths.Combine(root, "venvs")))
            {
                using var metadata = ConfigReaders.ReadJson(m.FileSystem, Paths.Combine(venv.FullPath, "pipx_metadata.json"));
                string? version = null;
                if (metadata?.RootElement.TryGetProperty("main_package", out var main) == true)
                {
                    version = ConfigReaders.String(main, "package_version");
                }

                Add("pipx", venv.Name, version, InstallScope.User, venv.FullPath);
            }
        }

        // uv tools.
        foreach (var tool in Dirs(m.GetEnvironmentVariable("UV_TOOL_DIR") ?? Paths.Combine(f.RoamingAppData, "uv", "tools")))
        {
            Add("uv", tool.Name, null, InstallScope.User, tool.FullPath);
        }

        // pip packages installed into user-level Python environments.
        var pythonRoots = Dirs(Paths.Combine(f.LocalAppData, "Programs", "Python")).Select(p => Paths.Combine(p.FullPath, "Lib", "site-packages"))
            .Concat(Dirs(Paths.Combine(f.RoamingAppData, "Python")).Select(p => Paths.Combine(p.FullPath, "site-packages")));
        foreach (var sitePackages in pythonRoots)
        {
            foreach (var distInfo in Dirs(sitePackages).Where(d => d.Name.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase)))
            {
                var stem = distInfo.Name[..^".dist-info".Length];
                var dash = stem.LastIndexOf('-');
                var name = dash > 0 ? stem[..dash] : stem;
                if (name is "pip" or "setuptools" or "wheel")
                {
                    continue;
                }

                Add("pip", name, dash > 0 ? stem[(dash + 1)..] : null, InstallScope.User, distInfo.FullPath);
            }
        }

        return Task.FromResult(new DiscoveryResult(items, [coverage.Build()]));
    }

    [GeneratedRegex(@"<version>\s*([^<\s]+)\s*</version>", RegexOptions.IgnoreCase)]
    private static partial Regex NuspecVersion();
}
