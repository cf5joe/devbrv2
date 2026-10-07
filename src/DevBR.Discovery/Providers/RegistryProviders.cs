using DevBR.Application.Discovery;
using DevBR.Application.Machine;
using DevBR.Discovery.Catalog;
using DevBR.Discovery.Support;
using DevBR.Domain;

namespace DevBR.Discovery.Providers;

/// <summary>
/// Add/Remove Programs records from HKLM and HKCU in both the 64-bit and 32-bit registry views.
/// Win32_Product is deliberately never used: enumerating it can trigger MSI repairs.
/// </summary>
public sealed class UninstallRegistryProvider : IDiscoveryProvider
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public string Id => "uninstall-registry";

    public string DisplayName => "Installed programs (registry)";

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        var coverage = new CoverageBuilder(DisplayName);
        var items = new List<InventoryItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.CurrentUser, RegistryView.Registry32) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = $"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}{(view == RegistryView.Registry64 ? "64" : "32")}\\{UninstallKey}";

            IRegistryKey? root;
            try
            {
                root = machine.Registry.OpenKey(hive, view, UninstallKey);
            }
            catch (UnauthorizedAccessException)
            {
                coverage.Inaccessible(label, "Access to the registry key was denied.");
                continue;
            }

            using (root)
            {
                if (root is null)
                {
                    continue;
                }

                coverage.CountDirectory();
                foreach (var name in root.GetSubKeyNames())
                {
                    // HKCU's Software key is shared between views; count each record once.
                    var dedupeKey = hive == RegistryHive.CurrentUser ? $"HKCU|{name}" : $"{label}|{name}";
                    if (!seen.Add(dedupeKey))
                    {
                        continue;
                    }

                    try
                    {
                        using var key = root.OpenSubKey(name);
                        if (key is not null && ReadEntry(machine, key, hive, label, name, coverage) is { } item)
                        {
                            items.Add(item);
                            context.OnItemFound?.Invoke(item);
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        coverage.Inaccessible($@"{label}\{name}", "Access to the registry key was denied.");
                    }
                }
            }
        }

        coverage.CountFiles(items.Count);
        return Task.FromResult(new DiscoveryResult(items, [coverage.Build()]));
    }

    private static InventoryItem? ReadEntry(IMachine machine, IRegistryKey key, RegistryHive hive, string label, string name, CoverageBuilder coverage)
    {
        var displayName = key.GetValue("DisplayName")?.AsString()?.Trim();
        if (string.IsNullOrEmpty(displayName))
        {
            return null;
        }

        if (key.GetValue("SystemComponent")?.AsInt64() == 1)
        {
            coverage.Exclude(displayName, "Hidden system component");
            return null;
        }

        var releaseType = key.GetValue("ReleaseType")?.AsString();
        if (key.GetValue("ParentKeyName") is not null || releaseType is "Update" or "Hotfix" or "Security Update")
        {
            coverage.Exclude(displayName, "Update or hotfix for another product");
            return null;
        }

        var installLocation = Clean(key.GetValue("InstallLocation")?.AsString(), machine);
        var icon = Clean(key.GetValue("DisplayIcon")?.AsString(), machine);
        var location = installLocation;
        // Icons inside installer caches say nothing about where the product lives.
        var cachedIcon = icon is not null && (icon.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase) || icon.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase));
        if (location is null && icon is not null && !cachedIcon && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            location = Paths.Parent(icon);
        }

        var tool = KnownTools.ByUninstallName(displayName);
        var version = key.GetValue("DisplayVersion")?.AsString();
        var properties = new Dictionary<string, string> { ["registeredName"] = displayName, ["registryKey"] = name };

        return ItemFactory.Installation(
            machine,
            tool,
            displayName,
            version,
            key.GetValue("Publisher")?.AsString(),
            hive == RegistryHive.CurrentUser ? InstallScope.User : InstallScope.Machine,
            location,
            new DiscoveryEvidence($"registry:{label}", $"Add/Remove Programs record \"{displayName}\"{(version is null ? string.Empty : " " + version)}", location),
            Confidence.High,
            properties);
    }

    private static string? Clean(string? value, IMachine machine)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = machine.Expand(value.Trim().Trim('"'));
        var comma = cleaned.LastIndexOf(',');
        if (comma > 2 && int.TryParse(cleaned[(comma + 1)..], out _))
        {
            cleaned = cleaned[..comma].Trim('"');
        }

        return cleaned.Length >= 3 && cleaned[1] == ':' ? Paths.Normalize(cleaned) : null;
    }
}

/// <summary>MSIX/AppX packages registered for the current user, via the Windows packaging API.</summary>
public sealed class StorePackageProvider : IDiscoveryProvider
{
    public string Id => "store-packages";

    public string DisplayName => "Store and packaged apps";

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        var coverage = new CoverageBuilder(DisplayName);
        var items = new List<InventoryItem>();

        IReadOnlyList<StorePackage> packages;
        try
        {
            packages = context.Machine.StorePackages.GetPackagesForCurrentUser();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or TypeLoadException)
        {
            coverage.Error($"Packaged apps could not be listed: {ex.Message}");
            return Task.FromResult(new DiscoveryResult([], [coverage.Build()]));
        }

        foreach (var package in packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tool = KnownTools.ByStorePackage(package.Name);
            var item = ItemFactory.Installation(
                context.Machine,
                tool,
                package.DisplayName,
                package.Version,
                package.Publisher,
                InstallScope.Store,
                package.InstallLocation,
                new DiscoveryEvidence("packages:current-user", $"Registered package {package.FamilyName}", package.InstallLocation),
                Confidence.Confirmed,
                new Dictionary<string, string> { ["packageFamilyName"] = package.FamilyName, ["packageFullName"] = package.FullName });

            items.Add(item);
            context.OnItemFound?.Invoke(item);
        }

        coverage.CountFiles(packages.Count);
        return Task.FromResult(new DiscoveryResult(items, [coverage.Build()]));
    }
}

/// <summary>App Paths registrations for known developer tools.</summary>
public sealed class AppPathsProvider : IDiscoveryProvider
{
    private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    public string Id => "app-paths";

    public string DisplayName => "App Paths registrations";

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        var machine = context.Machine;
        var coverage = new CoverageBuilder(DisplayName);
        var items = new List<InventoryItem>();

        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            try
            {
                using var root = machine.Registry.OpenKey(hive, view, AppPathsKey);
                if (root is null)
                {
                    continue;
                }

                foreach (var exeName in root.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    coverage.CountFiles(1);
                    if (!KnownTools.ExecutableNames.Contains(exeName))
                    {
                        continue;
                    }

                    using var key = root.OpenSubKey(exeName);
                    var path = key?.GetValue(string.Empty)?.AsString();
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    path = Paths.Normalize(machine.Expand(path.Trim().Trim('"')));
                    if (KnownTools.ByExecutableName(exeName, Paths.Parent(path)) is not { } tool)
                    {
                        continue;
                    }
                    var item = ItemFactory.Installation(machine, tool, tool.DisplayName, machine.FileSystem.GetFileVersion(path), null,
                        hive == RegistryHive.CurrentUser ? InstallScope.User : ItemFactory.ScopeForPath(machine.Folders, path),
                        Paths.Parent(path), new DiscoveryEvidence("registry:App Paths", $"{exeName} registered at {path}", path), Confidence.High);
                    items.Add(item);
                    context.OnItemFound?.Invoke(item);
                }
            }
            catch (UnauthorizedAccessException)
            {
                coverage.Inaccessible($"{hive}\\{AppPathsKey}", "Access to the registry key was denied.");
            }
        }

        return Task.FromResult(new DiscoveryResult(items, [coverage.Build()]));
    }
}

/// <summary>User and machine environment variables, with raw values and registry types preserved.</summary>
public sealed class EnvironmentProvider : IDiscoveryProvider
{
    public string Id => "environment";

    public string DisplayName => "Environment variables";

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        var coverage = new CoverageBuilder(DisplayName);
        var items = new List<InventoryItem>();

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            IReadOnlyDictionary<string, RegistryValue> variables;
            try
            {
                variables = context.Machine.ReadEnvironment(hive);
            }
            catch (UnauthorizedAccessException)
            {
                coverage.Inaccessible(hive == RegistryHive.CurrentUser ? @"HKCU\Environment" : @"HKLM\…\Session Manager\Environment", "Access denied.");
                continue;
            }

            var scope = hive == RegistryHive.CurrentUser ? InstallScope.User : InstallScope.Machine;
            foreach (var (name, value) in variables.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
            {
                var raw = value.AsString() ?? string.Empty;
                var secret = SecretDetector.IsSecret(name, raw);
                var properties = new Dictionary<string, string>
                {
                    ["registryType"] = value.Kind == RegistryValueKind.ExpandString ? "REG_EXPAND_SZ" : value.Kind == RegistryValueKind.String ? "REG_SZ" : value.Kind.ToString(),
                    ["scope"] = scope == InstallScope.User ? "User" : "Machine",
                    ["sensitive"] = secret ? "true" : "false",
                    // Secret values never reach the catalog; capture reads them again only if the user includes them.
                    ["value"] = secret ? "[hidden: looks like a credential]" : Truncate(raw),
                };

                if (string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase))
                {
                    properties["entries"] = raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                var item = new InventoryItem(
                    ItemIds.For("EnvironmentVariable", name, scope.ToString()),
                    InventoryCategory.EnvironmentVariable,
                    name,
                    null,
                    null,
                    scope,
                    [],
                    [new DiscoveryEvidence(scope == InstallScope.User ? @"registry:HKCU\Environment" : @"registry:HKLM\…\Session Manager\Environment", $"{properties["registryType"]} value")],
                    Confidence.Confirmed,
                    DetectionStatus.ConfirmedInstalled,
                    "environment",
                    null,
                    properties);
                items.Add(item);
            }

            coverage.CountFiles(variables.Count);
        }

        return Task.FromResult(new DiscoveryResult(items, [coverage.Build()]));
    }

    private static string Truncate(string value) => value.Length <= 2048 ? value : value[..2048] + "…";
}

/// <summary>Operating system, architecture and drive facts needed for compatibility checks.</summary>
public sealed class SystemFactsProvider : IDiscoveryProvider
{
    public string Id => "system-facts";

    public string DisplayName => "System facts";

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        var info = context.Machine.Info;
        var items = new List<InventoryItem>
        {
            Fact("Windows", $"{info.OsProductName} {info.OsDisplayVersion}".Trim(), info.OsBuild, new() { ["architecture"] = info.Architecture, ["computerName"] = info.ComputerName, ["user"] = info.UserName }),
        };

        foreach (var drive in context.Machine.FileSystem.GetFixedDrives())
        {
            items.Add(Fact($"Drive {drive.Root}", $"Drive {drive.Root.TrimEnd('\\')} {(string.IsNullOrEmpty(drive.Label) ? string.Empty : $"({drive.Label})")}".Trim(), null, new()
            {
                ["fileSystem"] = drive.Format ?? "unknown",
                ["totalBytes"] = drive.TotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["freeBytes"] = drive.FreeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }, drive.Root));
        }

        var coverage = new CoverageBuilder(DisplayName);
        coverage.CountFiles(items.Count);
        return Task.FromResult(new DiscoveryResult(items, [coverage.Build()]));
    }

    private static InventoryItem Fact(string key, string name, string? version, Dictionary<string, string> properties, string? location = null)
        => new(ItemIds.For("SystemFact", key), InventoryCategory.SystemFact, name, version, null, InstallScope.Machine,
            location is null ? [] : [location], [new DiscoveryEvidence("system", "Read from the operating system")], Confidence.Confirmed,
            DetectionStatus.ConfirmedInstalled, null, null, properties);
}
