using System.Text.Json;
using DevBR.Discovery.Support;
using DevBR.Domain;

namespace DevBR.Discovery.Adapters;

/// <summary>
/// VS Code (Stable or Insiders): user settings, keybindings, snippets, tasks, MCP servers, chat
/// prompt/instruction files, every named profile kept in its own scope, and the extension inventory.
/// </summary>
public sealed class VsCodeAdapter(bool insiders) : ToolAdapter
{
    private static readonly string[] Excluded =
    [
        "workspaceStorage and globalStorage state databases",
        "History, backups, logs and caches",
        "Settings Sync machine state",
    ];

    public override string Id => insiders ? "vscode-insiders" : "vscode";

    public override string DisplayName => insiders ? "VS Code Insiders" : "VS Code";

    public override IReadOnlyList<string> SupportedVersions => ["1.90 and later"];

    protected override string ToolId => Id;

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        var product = insiders ? "Code - Insiders" : "Code";
        var userDir = s.Combine(s.Folders.RoamingAppData, product, "User");
        var extensionsDir = s.Combine(s.Folders.UserProfile, insiders ? ".vscode-insiders" : ".vscode", "extensions");
        if (!s.Exists(userDir) && !s.Exists(extensionsDir))
        {
            return;
        }

        var mcpServers = ConfigReaders.McpServerNames(s.Fs, s.Combine(userDir, "mcp.json"), "servers", "mcpServers");
        s.ConfigurationItem($"{DisplayName} user configuration", userDir, new Dictionary<string, string>
        {
            ["mcpServers"] = string.Join(", ", mcpServers),
        });

        DescribeUserFolder(s, userDir, "default", "Default profile");

        // Named profiles live in User\profiles\<location> and are listed in globalStorage\storage.json.
        using var storage = ConfigReaders.ReadJson(s.Fs, s.Combine(userDir, "globalStorage", "storage.json"));
        if (storage?.RootElement.TryGetProperty("userDataProfiles", out var profiles) == true && profiles.ValueKind == JsonValueKind.Array)
        {
            foreach (var profile in profiles.EnumerateArray())
            {
                var location = ConfigReaders.String(profile, "location");
                var name = ConfigReaders.String(profile, "name") ?? location;
                if (location is not null)
                {
                    DescribeUserFolder(s, s.Combine(userDir, "profiles", location), $"profile-{location}", $"Profile \"{name}\"");
                }
            }
        }

        // Extension inventory (identifiers and versions) for reinstallation; extension binaries are not copied.
        var extensions = ReadExtensions(s, extensionsDir);
        foreach (var (id, version) in extensions)
        {
            s.Item(new InventoryItem(
                ItemIds.For("Extension", $"{Id}-{id}"),
                InventoryCategory.Extension,
                id,
                version,
                id.Split('.')[0],
                InstallScope.User,
                [extensionsDir],
                [new DiscoveryEvidence($"adapter:{Id}", "Listed in extensions.json", extensionsDir)],
                Confidence.Confirmed,
                DetectionStatus.ConfirmedInstalled,
                Id,
                ToolId));
        }

        if (extensions.Count > 0)
        {
            s.AddArtifact(new MigrationArtifact(
                $"{Id}:extensions", ToolId, ArtifactKind.ExtensionInventory, $"{DisplayName} extensions",
                [Paths.ToLogical(s.Folders, s.Combine(extensionsDir, "extensions.json"))], Sensitivity.None, BackupEligibility.Eligible,
                RestoreCapability.DependencyInstall | RestoreCapability.ReinstallGuidance, [], false,
                $"{AdapterScope.Count(extensions.Count, "extension", "extensions")} recorded for reinstallation from the marketplace.",
                ["Extension binaries (reinstalled from their source instead)"]));
        }
    }

    private void DescribeUserFolder(AdapterScope s, string folder, string key, string label)
    {
        if (!s.Exists(folder))
        {
            return;
        }

        var prefix = key == "default" ? string.Empty : $"{label}: ";
        s.Artifact($"{key}:settings", ArtifactKind.Settings, $"{prefix}Settings",
            [s.Combine(folder, "settings.json"), s.Combine(folder, "tasks.json")], "User settings and tasks.", excluded: key == "default" ? Excluded : null);
        s.Artifact($"{key}:keybindings", ArtifactKind.Keybindings, $"{prefix}Keybindings", s.Combine(folder, "keybindings.json"), "Custom keyboard shortcuts.");
        s.Artifact($"{key}:snippets", ArtifactKind.Snippets, $"{prefix}Snippets", s.Combine(folder, "snippets"), "User code snippets.");
        s.Artifact($"{key}:mcp", ArtifactKind.McpConfiguration, $"{prefix}MCP servers", s.Combine(folder, "mcp.json"),
            "MCP server definitions. Commands are restored as configuration only, never run during restore.", Sensitivity.MayContainSecrets,
            capability: RestoreCapability.ExplicitFileRestore | RestoreCapability.StructuredMerge);
        s.Artifact($"{key}:prompts", ArtifactKind.Instructions, $"{prefix}Chat prompts and instructions", s.Combine(folder, "prompts"),
            "Copilot Chat prompt files, instructions and custom chat modes.");
    }

    private static List<(string Id, string? Version)> ReadExtensions(AdapterScope s, string extensionsDir)
    {
        var result = new List<(string, string?)>();
        using var manifest = ConfigReaders.ReadJson(s.Fs, s.Combine(extensionsDir, "extensions.json"));
        if (manifest?.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var extension in manifest.RootElement.EnumerateArray())
            {
                if (extension.TryGetProperty("identifier", out var identifier) && ConfigReaders.String(identifier, "id") is { } id)
                {
                    result.Add((id, ConfigReaders.String(extension, "version")));
                }
            }

            return result;
        }

        // Older layouts: one "publisher.name-1.2.3" folder per extension.
        foreach (var directory in s.Children(extensionsDir).Where(e => e.IsDirectory))
        {
            var dash = directory.Name.LastIndexOf('-');
            result.Add(dash > 0 ? (directory.Name[..dash], directory.Name[(dash + 1)..]) : (directory.Name, null));
        }

        return result;
    }
}

/// <summary>Cursor: editor settings, keybindings, snippets, MCP servers, rules, skills, hooks and extension inventory.</summary>
public sealed class CursorAdapter : ToolAdapter
{
    public override string Id => "cursor";

    public override string DisplayName => "Cursor";

    protected override string ToolId => "cursor";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        var userDir = s.Combine(s.Folders.RoamingAppData, "Cursor", "User");
        var home = s.Combine(s.Folders.UserProfile, ".cursor");
        if (!s.Exists(userDir) && !s.Exists(home))
        {
            return;
        }

        s.ConfigurationItem("Cursor user configuration", s.Exists(home) ? home : userDir, new Dictionary<string, string>
        {
            ["mcpServers"] = string.Join(", ", ConfigReaders.McpServerNames(s.Fs, s.Combine(home, "mcp.json"), "mcpServers")),
        });

        s.Artifact("settings", ArtifactKind.Settings, "Settings", [s.Combine(userDir, "settings.json")], "Editor settings.",
            excluded: ["workspaceStorage and globalStorage state databases", "Chat history, logs and caches", "User rules stored in Cursor's internal database (manual)"]);
        s.Artifact("keybindings", ArtifactKind.Keybindings, "Keybindings", s.Combine(userDir, "keybindings.json"), "Custom keyboard shortcuts.");
        s.Artifact("snippets", ArtifactKind.Snippets, "Snippets", s.Combine(userDir, "snippets"), "User code snippets.");
        s.Artifact("mcp", ArtifactKind.McpConfiguration, "MCP servers", s.Combine(home, "mcp.json"), "Global MCP server definitions.", Sensitivity.MayContainSecrets,
            capability: RestoreCapability.ExplicitFileRestore | RestoreCapability.StructuredMerge);
        s.Artifact("rules", ArtifactKind.Rules, "Rules", s.Combine(home, "rules"), "Global rule files.");
        s.Artifact("skills", ArtifactKind.Skills, "Skills", s.Combine(home, "skills"), "User skills.");
        s.Artifact("commands", ArtifactKind.Commands, "Commands", s.Combine(home, "commands"), "Custom commands.");
        s.Artifact("hooks", ArtifactKind.Hooks, "Hooks", [s.Combine(home, "hooks.json"), s.Combine(home, "hooks")], "Agent hooks. Hook scripts are copied, never run during discovery or restore.");

        var extensions = s.Combine(home, "extensions", "extensions.json");
        if (s.Exists(extensions))
        {
            s.AddArtifact(new MigrationArtifact("cursor:extensions", ToolId, ArtifactKind.ExtensionInventory, "Extensions",
                [Paths.ToLogical(s.Folders, extensions)], Sensitivity.None, BackupEligibility.Eligible,
                RestoreCapability.DependencyInstall | RestoreCapability.ReinstallGuidance, [], false, "Extension list for reinstallation.",
                ["Extension binaries"]));
        }
    }
}
