using System.Text.Json;
using System.Text.RegularExpressions;
using DevBR.Application.Machine;
using DevBR.Discovery.Support;
using DevBR.Domain;

namespace DevBR.Discovery.Adapters;

/// <summary>Git global configuration, its include files, ignore/attributes files and credential storage.</summary>
public sealed class GitAdapter : ToolAdapter
{
    public override string Id => "git";

    public override string DisplayName => "Git";

    protected override string ToolId => "git";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        var (global, _) = s.ResolveRoot(s.Combine(s.Folders.UserProfile, ".gitconfig"), "GIT_CONFIG_GLOBAL");
        var xdgRoot = s.Machine.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? s.Combine(s.Folders.UserProfile, ".config");
        var xdgGit = s.Combine(xdgRoot, "git");

        var paths = new List<string> { global, s.Combine(xdgGit, "config") };
        var config = GitConfig.Parse(s.Fs.ReadText(global, ConfigReaders.MaxConfigBytes));

        // Included files and referenced ignore/attributes files travel with the configuration.
        foreach (var include in config.IncludePaths())
        {
            paths.Add(ExpandHome(s, include));
        }

        var helper = config.Get("credential", null, "helper");
        var referenced = new[] { config.Get("core", null, "excludesfile"), config.Get("core", null, "attributesfile") }
            .OfType<string>().Select(p => ExpandHome(s, p)).Append(s.Combine(xdgGit, "ignore")).Append(s.Combine(xdgGit, "attributes"));

        if (!paths.Concat(referenced).Any(s.Exists))
        {
            return;
        }

        s.ConfigurationItem("Git global configuration", s.Exists(global) ? global : xdgGit, new Dictionary<string, string>
        {
            ["userName"] = config.Get("user", null, "name") ?? string.Empty,
            ["credentialHelper"] = helper ?? "none",
            ["includes"] = config.IncludePaths().Count().ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

        s.Artifact("config", ArtifactKind.Settings, "Global configuration and includes", paths,
            "user.name/email, aliases, core settings and included configuration files.",
            excluded: ["Stored credentials (sign in again, or include them explicitly)"]);
        s.Artifact("ignore", ArtifactKind.Settings, "Global ignore and attributes files", referenced,
            "Files referenced by core.excludesfile and core.attributesfile.");
        s.Credential("credentials-store", "Stored credentials (.git-credentials)", s.Combine(s.Folders.UserProfile, ".git-credentials"),
            "Plain-text credentials written by the 'store' helper.");

        // Machine-wide configuration of the installed Git: inventory only.
        foreach (var system in new[] { s.Combine(s.Folders.ProgramFiles, "Git", "etc", "gitconfig") }.Where(s.Exists))
        {
            s.ConfigurationItem("Git system configuration (installation default)", system);
        }
    }

    private static string ExpandHome(AdapterScope s, string path)
    {
        var expanded = path.StartsWith('~') ? s.Folders.UserProfile + path[1..] : path;
        expanded = s.Machine.Expand(expanded.Replace('/', '\\'));
        return expanded.Length >= 2 && expanded[1] == ':' ? Paths.Normalize(expanded) : s.Combine(s.Folders.UserProfile, expanded);
    }
}

/// <summary>GitHub CLI preferences and aliases; host authentication is excluded.</summary>
public sealed class GitHubCliAdapter : ToolAdapter
{
    public override string Id => "gh";

    public override string DisplayName => "GitHub CLI";

    protected override string ToolId => "gh";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        var (root, _) = s.ResolveRoot(s.Combine(s.Folders.RoamingAppData, "GitHub CLI"), "GH_CONFIG_DIR");
        if (!s.Exists(root))
        {
            return;
        }

        s.ConfigurationItem("GitHub CLI configuration", root);
        s.Artifact("config", ArtifactKind.Settings, "Preferences and aliases", s.Combine(root, "config.yml"), "Editor, protocol and alias preferences.",
            excluded: ["Host authentication (run 'gh auth login' on the new computer)"]);

        var hosts = s.Combine(root, "hosts.yml");
        var hostsText = s.Fs.ReadText(hosts, 1024 * 1024);
        if (hostsText?.Contains("oauth_token", StringComparison.Ordinal) == true)
        {
            s.Credential("hosts", "Host tokens (hosts.yml)", hosts, "This file contains GitHub tokens stored outside the system keyring.");
        }
        else
        {
            s.Artifact("hosts", ArtifactKind.Settings, "Host list", hosts, "Configured hosts and Git protocol (tokens are in the system keyring).");
        }
    }
}

/// <summary>
/// Windows PowerShell 5.1 and PowerShell 7 profiles, scripts they dot-source, locally authored modules,
/// and an inventory of gallery-installed modules for reinstallation.
/// </summary>
public sealed partial class PowerShellAdapter : ToolAdapter
{
    private static readonly string[] ProfileNames = ["profile.ps1", "Microsoft.PowerShell_profile.ps1", "Microsoft.VSCode_profile.ps1"];

    public override string Id => "powershell";

    public override string DisplayName => "PowerShell";

    protected override string ToolId => "pwsh";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        foreach (var (folder, label, key) in new[] { ("PowerShell", "PowerShell 7", "ps7"), ("WindowsPowerShell", "Windows PowerShell", "ps5") })
        {
            var root = s.Combine(s.Folders.Documents, folder);
            if (!s.Exists(root))
            {
                continue;
            }

            var profiles = ProfileNames.Select(n => s.Combine(root, n)).Where(s.Exists).ToList();
            var referenced = profiles.SelectMany(p => ReferencedScripts(s, p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            s.Artifact($"{key}:profiles", ArtifactKind.Scripts, $"{label} profiles", profiles.Concat(referenced),
                referenced.Count == 0 ? "Profile scripts." : $"Profile scripts and {AdapterScope.Count(referenced.Count, "script", "scripts")} they load.");

            var custom = new List<string>();
            foreach (var module in s.Children(s.Combine(root, "Modules")).Where(m => m.IsDirectory))
            {
                if (IsGalleryModule(s, module.FullPath))
                {
                    var version = s.Children(module.FullPath).Where(v => v.IsDirectory && Version.TryParse(v.Name, out _)).Select(v => v.Name).Max(StringComparer.Ordinal);
                    s.Item(new InventoryItem(ItemIds.For("Package", $"psmodule-{module.Name}", module.FullPath), InventoryCategory.Package, module.Name, version, null,
                        InstallScope.User, [module.FullPath], [new DiscoveryEvidence($"adapter:{Id}", "Installed from the PowerShell Gallery (PSGetModuleInfo.xml)", module.FullPath)],
                        Confidence.Confirmed, DetectionStatus.ConfirmedInstalled, Id, ToolId, new Dictionary<string, string> { ["packageManager"] = "PowerShellGet", ["edition"] = label }));
                }
                else
                {
                    custom.Add(module.FullPath);
                }
            }

            s.Artifact($"{key}:modules", ArtifactKind.Modules, $"{label} custom modules", custom,
                $"{AdapterScope.Count(custom.Count, "locally authored module", "locally authored modules")} (gallery modules are reinstalled instead).");

            if (profiles.Count > 0 || custom.Count > 0)
            {
                s.ConfigurationItem($"{label} profile", root, new Dictionary<string, string>
                {
                    ["profiles"] = string.Join(", ", profiles.Select(Path.GetFileName)),
                    ["customModules"] = custom.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            }
        }
    }

    private static bool IsGalleryModule(AdapterScope s, string moduleRoot)
        => s.Exists(s.Combine(moduleRoot, "PSGetModuleInfo.xml"))
            || s.Children(moduleRoot).Any(v => v.IsDirectory && s.Exists(s.Combine(v.FullPath, "PSGetModuleInfo.xml")));

    /// <summary>Scripts dot-sourced or imported by path from a profile. The profile is read, never run.</summary>
    private static IEnumerable<string> ReferencedScripts(AdapterScope s, string profile)
    {
        var text = s.Fs.ReadText(profile, 1024 * 1024);
        if (text is null)
        {
            yield break;
        }

        var profileDir = Paths.Parent(profile)!;
        foreach (Match match in ScriptReference().Matches(text))
        {
            var raw = match.Groups["path"].Value.Trim('"', '\'');
            var path = raw
                .Replace("$PSScriptRoot", profileDir, StringComparison.OrdinalIgnoreCase)
                .Replace("$HOME", s.Folders.UserProfile, StringComparison.OrdinalIgnoreCase)
                .Replace("$env:USERPROFILE", s.Folders.UserProfile, StringComparison.OrdinalIgnoreCase)
                .Replace('/', '\\');
            if (path.StartsWith('~'))
            {
                path = s.Folders.UserProfile + path[1..];
            }

            if (path.Contains('$', StringComparison.Ordinal))
            {
                continue; // Unresolvable expression: reported as manual handling later, never evaluated.
            }

            var full = path.Length >= 2 && path[1] == ':' ? path : s.Combine(profileDir, path);
            if (s.Fs.FileExists(full))
            {
                yield return Paths.Normalize(Path.GetFullPath(full));
            }
        }
    }

    [GeneratedRegex(@"^\s*(?:\.|Import-Module)\s+(?<path>(?:""[^""]+""|'[^']+'|\S+)\.(?:ps1|psm1|psd1)['""]?)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptReference();
}

/// <summary>Windows Terminal (Store, Preview and unpackaged): settings.json, fragments and local assets.</summary>
public sealed class WindowsTerminalAdapter : ToolAdapter
{
    public override string Id => "windows-terminal";

    public override string DisplayName => "Windows Terminal";

    public override IReadOnlyList<string> SupportedVersions => ["1.18 and later"];

    protected override string ToolId => "windows-terminal";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        var packages = s.Combine(s.Folders.LocalAppData, "Packages");
        var installs = new[]
        {
            ("stable", "Windows Terminal", s.Combine(packages, "Microsoft.WindowsTerminal_8wekyb3d8bbwe")),
            ("preview", "Windows Terminal Preview", s.Combine(packages, "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe")),
        };

        foreach (var (key, label, package) in installs)
        {
            var settings = s.Combine(package, "LocalState", "settings.json");
            if (!s.Exists(settings))
            {
                continue;
            }

            using var json = ConfigReaders.ReadJson(s.Fs, settings);
            var profiles = 0;
            var schemes = 0;
            if (json is not null)
            {
                if (json.RootElement.TryGetProperty("profiles", out var p))
                {
                    profiles = p.ValueKind == JsonValueKind.Array ? p.GetArrayLength()
                        : p.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array ? list.GetArrayLength() : 0;
                }

                if (json.RootElement.TryGetProperty("schemes", out var sc) && sc.ValueKind == JsonValueKind.Array)
                {
                    schemes = sc.GetArrayLength();
                }
            }

            s.ConfigurationItem($"{label} settings", settings, new Dictionary<string, string>
            {
                ["profiles"] = profiles.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["schemes"] = schemes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

            s.Artifact($"{key}:settings", ArtifactKind.Settings, $"{label} settings", settings,
                $"{AdapterScope.Count(profiles, "profile", "profiles")} and {AdapterScope.Count(schemes, "color scheme", "color schemes")}.",
                capability: RestoreCapability.ExplicitFileRestore | RestoreCapability.StructuredMerge,
                excluded: ["Window state (state.json)", "Generated profiles for tools not installed on the new computer are kept but may show as unavailable"]);
            s.Artifact($"{key}:assets", ArtifactKind.Assets, $"{label} local assets", s.Combine(package, "RoamingState"),
                "Background images and icons referenced through ms-appdata:///roaming/.");
        }

        var unpackaged = s.Combine(s.Folders.LocalAppData, "Microsoft", "Windows Terminal");
        s.Artifact("unpackaged:settings", ArtifactKind.Settings, "Windows Terminal (unpackaged) settings", s.Combine(unpackaged, "settings.json"), "Settings of an unpackaged install.");
        s.Artifact("fragments", ArtifactKind.Settings, "Settings fragments", s.Combine(unpackaged, "Fragments"), "Profile and scheme fragments added by you or other tools.");
    }
}

/// <summary>WSL: distribution inventory and the Windows-side .wslconfig. Distribution disks are never exported.</summary>
public sealed class WslAdapter : ToolAdapter
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public override string Id => "wsl";

    public override string DisplayName => "WSL";

    protected override string ToolId => "wsl";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        var distributions = new List<string>();
        try
        {
            using var lxss = s.Machine.Registry.OpenKey(RegistryHive.CurrentUser, RegistryView.Registry64, LxssKey);
            var defaultId = lxss?.GetValue("DefaultDistribution")?.AsString();
            foreach (var id in lxss?.GetSubKeyNames() ?? [])
            {
                using var distro = lxss!.OpenSubKey(id);
                var name = distro?.GetValue("DistributionName")?.AsString();
                if (name is null)
                {
                    continue;
                }

                distributions.Add(name);
                var basePath = distro!.GetValue("BasePath")?.AsString();
                s.Item(new InventoryItem(ItemIds.For("Runtime", $"wsl-{name}", id), InventoryCategory.Runtime, $"WSL distribution: {name}",
                    distro.GetValue("Version")?.AsInt64() is { } v ? $"WSL {v}" : null, null, InstallScope.User,
                    basePath is null ? [] : [Paths.Normalize(s.Machine.Expand(basePath.TrimStart('\\', '?')))],
                    [new DiscoveryEvidence(@"registry:HKCU\…\Lxss", $"Registered distribution {id}")], Confidence.Confirmed, DetectionStatus.ConfirmedInstalled,
                    Id, ToolId, new Dictionary<string, string> { ["default"] = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase) ? "true" : "false" }));
            }
        }
        catch (UnauthorizedAccessException)
        {
            s.Coverage.Inaccessible($@"HKCU\{LxssKey}", "Access denied.");
        }

        if (distributions.Count > 0)
        {
            s.AddArtifact(new MigrationArtifact("wsl:distributions", ToolId, ArtifactKind.Inventory, "WSL distributions (inventory)", [], Sensitivity.None,
                BackupEligibility.InventoryOnly, RestoreCapability.ReinstallGuidance, [], false,
                $"{string.Join(", ", distributions)}. Recorded with reinstall guidance.",
                ["Distribution file systems (ext4.vhdx) are not exported; use 'wsl --export' separately if needed"]));
        }

        s.Artifact("wslconfig", ArtifactKind.Settings, ".wslconfig", s.Combine(s.Folders.UserProfile, ".wslconfig"), "Windows-side WSL settings (memory, processors, networking).");
    }
}

/// <summary>Docker: Windows-side client and Desktop settings. Images, containers and volumes are never exported.</summary>
public sealed class DockerAdapter : ToolAdapter
{
    public override string Id => "docker";

    public override string DisplayName => "Docker";

    protected override string ToolId => "docker-desktop";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        var (dockerConfig, _) = s.ResolveRoot(s.Combine(s.Folders.UserProfile, ".docker"), "DOCKER_CONFIG");
        var desktop = s.Combine(s.Folders.RoamingAppData, "Docker");
        if (!s.Exists(dockerConfig) && !s.Exists(desktop))
        {
            return;
        }

        var configFile = s.Combine(dockerConfig, "config.json");
        var hasInlineAuth = false;
        using (var json = ConfigReaders.ReadJson(s.Fs, configFile))
        {
            if (json?.RootElement.TryGetProperty("auths", out var auths) == true && auths.ValueKind == JsonValueKind.Object)
            {
                hasInlineAuth = auths.EnumerateObject().Any(a => a.Value.ValueKind == JsonValueKind.Object && a.Value.TryGetProperty("auth", out _));
            }
        }

        s.ConfigurationItem("Docker client configuration", s.Exists(dockerConfig) ? dockerConfig : desktop, new Dictionary<string, string> { ["inlineRegistryAuth"] = hasInlineAuth ? "true" : "false" });
        s.Artifact("client-config", ArtifactKind.Settings, "Client configuration", configFile,
            hasInlineAuth ? "CLI settings. Registry credentials inside are removed at capture." : "CLI settings and credential-helper choice.",
            hasInlineAuth ? Sensitivity.ContainsRecognizedSecrets : Sensitivity.MayContainSecrets,
            excluded: ["Images, containers, volumes and build cache", "Registry sign-ins (run 'docker login' again)"]);
        s.Artifact("contexts", ArtifactKind.Settings, "Contexts", s.Combine(dockerConfig, "contexts", "meta"), "Docker context definitions (endpoints only; TLS material is excluded).");
        s.Artifact("daemon", ArtifactKind.Settings, "Engine configuration (daemon.json)", s.Combine(dockerConfig, "daemon.json"), "Engine settings.");
        s.Artifact("desktop-settings", ArtifactKind.Settings, "Docker Desktop settings", [s.Combine(desktop, "settings-store.json"), s.Combine(desktop, "settings.json")],
            "Resources, WSL integration and feature toggles.");
    }
}

/// <summary>
/// The Windows environment baseline: non-secret machine variables (selected by default), user variables
/// and PATH (optional), and recognized secret variables (excluded unless explicitly included).
/// </summary>
public sealed class EnvironmentAdapter : ToolAdapter
{
    public override string Id => "environment";

    public override string DisplayName => "Windows environment";

    protected override string ToolId => "windows";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            var variables = s.Machine.ReadEnvironment(hive);
            var secrets = variables.Where(v => SecretDetector.IsSecret(v.Key, v.Value.AsString())).Select(v => v.Key).ToList();
            var regular = variables.Keys.Except(secrets, StringComparer.OrdinalIgnoreCase).ToList();
            var isMachine = hive == RegistryHive.LocalMachine;

            if (regular.Count > 0)
            {
                s.AddArtifact(new MigrationArtifact(
                    isMachine ? "environment:machine" : "environment:user",
                    ToolId,
                    ArtifactKind.EnvironmentVariables,
                    isMachine ? "Machine environment variables" : "User environment variables and PATH",
                    [],
                    Sensitivity.None,
                    BackupEligibility.Eligible,
                    RestoreCapability.StructuredMerge,
                    [],
                    SelectedByDefault: isMachine,
                    isMachine
                        ? $"{AdapterScope.Count(regular.Count, "variable", "variables")} captured with raw values and types. Restore proposes only custom values; system-defined ones are kept."
                        : $"{AdapterScope.Count(regular.Count, "variable", "variables")}. PATH entries are appended without reordering the target's PATH.",
                    secrets.Count == 0 ? null : [$"{AdapterScope.Count(secrets.Count, "variable that looks like a credential", "variables that look like credentials")} (listed separately)"]));
            }

            foreach (var name in secrets)
            {
                s.AddArtifact(new MigrationArtifact(
                    $"environment:secret:{(isMachine ? "machine" : "user")}:{name}", ToolId, ArtifactKind.Credentials,
                    $"{name} ({(isMachine ? "machine" : "user")} variable)", [], Sensitivity.Credential, BackupEligibility.ExcludedByDefault,
                    RestoreCapability.StructuredMerge, [], false, "Looks like a credential. Excluded unless you include it, which requires encryption."));
            }
        }
    }
}
