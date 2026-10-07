using System.Text.Json;
using DevBR.Application.Machine;
using DevBR.Discovery.Support;
using DevBR.Domain;

namespace DevBR.Discovery.Adapters;

/// <summary>
/// GitHub Copilot CLI (~/.copilot). Settings, MCP definitions, agents, skills, hooks and instructions
/// are captured selectively; session state, history and logs are not.
/// </summary>
public sealed class CopilotAdapter : ToolAdapter
{
    public override string Id => "copilot";

    public override string DisplayName => "GitHub Copilot CLI";

    protected override string ToolId => "copilot-cli";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        var xdg = s.Machine.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var (root, overridden) = s.ResolveRoot(xdg is null ? s.Combine(s.Folders.UserProfile, ".copilot") : s.Combine(xdg, ".copilot"), "COPILOT_HOME");
        if (!s.Exists(root))
        {
            return;
        }

        var key = s.Combine(root);
        s.ConfigurationItem("Copilot CLI configuration", root, new Dictionary<string, string>
        {
            ["mcpServers"] = string.Join(", ", ConfigReaders.McpServerNames(s.Fs, s.Combine(root, "mcp-config.json"), "mcpServers")),
            ["location"] = overridden is null ? "default" : $"from {overridden}",
        });

        s.Artifact("settings", ArtifactKind.Settings, "Settings", [s.Combine(root, "config.json")],
            "CLI preferences. Remembered folder-trust approvals are removed at capture.", excluded:
            ["Session state and history", "Logs", "Folder-trust approvals", "Authentication (sign in again on the new computer)"], customRootKey: key);
        s.Artifact("mcp", ArtifactKind.McpConfiguration, "MCP servers", s.Combine(root, "mcp-config.json"), "MCP server definitions.", Sensitivity.MayContainSecrets,
            capability: RestoreCapability.ExplicitFileRestore | RestoreCapability.StructuredMerge, customRootKey: key);
        s.Artifact("agents", ArtifactKind.Agents, "Custom agents", s.Combine(root, "agents"), "Agent profiles.", customRootKey: key);
        s.Artifact("skills", ArtifactKind.Skills, "Skills", s.Combine(root, "skills"), "User skills.", customRootKey: key);
        s.Artifact("hooks", ArtifactKind.Hooks, "Hooks", s.Combine(root, "hooks"), "Hook definitions and scripts (copied, never run).", customRootKey: key);
        s.Artifact("instructions", ArtifactKind.Instructions, "Instructions", s.Combine(root, "copilot-instructions.md"), "Personal instructions.", customRootKey: key);
        s.Artifact("plugins", ArtifactKind.Plugins, "Plugins", s.Combine(root, "plugins"), "Installed plugin references and local plugin sources.", customRootKey: key);
    }
}

/// <summary>Codex (CODEX_HOME or ~/.codex): config.toml incl. MCP servers, AGENTS.md, prompts, skills and local plugins.</summary>
public sealed class CodexAdapter : ToolAdapter
{
    public override string Id => "codex";

    public override string DisplayName => "Codex";

    protected override string ToolId => "codex";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        var (root, overridden) = s.ResolveRoot(s.Combine(s.Folders.UserProfile, ".codex"), "CODEX_HOME");
        if (!s.Exists(root))
        {
            return;
        }

        var configPath = s.Combine(root, "config.toml");
        var mcp = new List<string>();
        if (ConfigReaders.ReadToml(s.Fs, configPath) is { } config && config.TryGetValue("mcp_servers", out var servers) && servers is Tomlyn.Model.TomlTable table)
        {
            mcp.AddRange(table.Keys);
        }

        s.ConfigurationItem("Codex configuration", root, new Dictionary<string, string>
        {
            ["mcpServers"] = string.Join(", ", mcp),
            ["location"] = overridden is null ? "default" : $"from {overridden}",
        });

        var key = root;
        s.Artifact("config", ArtifactKind.Settings, "config.toml", [configPath],
            mcp.Count == 0 ? "Model, sandbox and approval settings." : $"Settings and {AdapterScope.Count(mcp.Count, "MCP server", "MCP servers")}.",
            Sensitivity.MayContainSecrets, capability: RestoreCapability.ExplicitFileRestore | RestoreCapability.StructuredMerge,
            excluded: ["Sessions and archived sessions", "history.jsonl and logs", "Caches and the downloaded plugin cache (plugins are referenced in config.toml)"], customRootKey: key);
        s.Artifact("agents-md", ArtifactKind.Instructions, "AGENTS.md", s.Combine(root, "AGENTS.md"), "Global agent instructions.", customRootKey: key);
        s.Artifact("prompts", ArtifactKind.Instructions, "Custom prompts", s.Combine(root, "prompts"), "Reusable prompts.", customRootKey: key);
        s.Artifact("skills", ArtifactKind.Skills, "Skills", s.Combine(root, "skills"), "User skills.", customRootKey: key);

        // Authentication may be file-based or in the OS credential store; transferring it is a separate, explicit choice.
        s.Credential("auth", "Sign-in (auth.json)", s.Combine(root, "auth.json"),
            "Codex sign-in. Excluded by default; signing in again on the new computer is recommended.", customRootKey: key);
    }
}

/// <summary>
/// Claude Code user scope (CLAUDE_CONFIG_DIR or ~/.claude, plus the user MCP servers in ~/.claude.json).
/// Project scopes (.claude folders, CLAUDE.md, .mcp.json) are found by the filesystem pass.
/// </summary>
public sealed class ClaudeCodeAdapter : ToolAdapter
{
    public override string Id => "claude-code";

    public override string DisplayName => "Claude Code";

    protected override string ToolId => "claude-code";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        var (root, overridden) = s.ResolveRoot(s.Combine(s.Folders.UserProfile, ".claude"), "CLAUDE_CONFIG_DIR");
        var stateFile = overridden is null ? s.Combine(s.Folders.UserProfile, ".claude.json") : s.Combine(root, ".claude.json");

        var managed = s.Combine(s.Folders.ProgramData, "ClaudeCode", "managed-settings.json");
        if (s.Exists(managed))
        {
            // Enterprise policy: inventoried so restore can respect it, never captured or overridden.
            s.ConfigurationItem("Claude Code managed settings (organization policy)", managed, new Dictionary<string, string> { ["managedPolicy"] = "true" });
        }

        if (!s.Exists(root) && !s.Exists(stateFile))
        {
            return;
        }

        var hooks = 0;
        using (var settings = ConfigReaders.ReadJson(s.Fs, s.Combine(root, "settings.json")))
        {
            if (settings?.RootElement.TryGetProperty("hooks", out var hookTable) == true && hookTable.ValueKind == JsonValueKind.Object)
            {
                hooks = hookTable.EnumerateObject().Sum(e => e.Value.ValueKind == JsonValueKind.Array ? e.Value.GetArrayLength() : 1);
            }
        }

        var userMcp = ConfigReaders.McpServerNames(s.Fs, stateFile, "mcpServers");
        s.ConfigurationItem("Claude Code user configuration", root, new Dictionary<string, string>
        {
            ["mcpServers"] = string.Join(", ", userMcp),
            ["hooks"] = hooks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["location"] = overridden is null ? "default" : $"from {overridden}",
        });

        var key = overridden is null ? null : root;
        s.Artifact("settings", ArtifactKind.Settings, "Settings", [s.Combine(root, "settings.json"), s.Combine(root, "settings.local.json")],
            hooks == 0 ? "User settings and permissions." : $"User settings, permissions and {AdapterScope.Count(hooks, "hook", "hooks")}.",
            excluded: ["Project session transcripts (projects/)", "Todos, shell snapshots, file history and debug logs", "Statistics and IDE lock files"], customRootKey: key);
        s.Artifact("claude-md", ArtifactKind.Instructions, "CLAUDE.md", s.Combine(root, "CLAUDE.md"), "Personal instructions loaded in every project.", customRootKey: key);
        s.Artifact("agents", ArtifactKind.Agents, "Subagents", s.Combine(root, "agents"), "User subagent definitions.", customRootKey: key);
        s.Artifact("skills", ArtifactKind.Skills, "Skills", s.Combine(root, "skills"), "User skills.", customRootKey: key);
        s.Artifact("commands", ArtifactKind.Commands, "Slash commands", s.Combine(root, "commands"), "Custom slash commands.", customRootKey: key);
        s.Artifact("output-styles", ArtifactKind.Instructions, "Output styles", s.Combine(root, "output-styles"), "Custom output styles.", customRootKey: key);
        s.Artifact("hooks", ArtifactKind.Hooks, "Hook scripts", s.Combine(root, "hooks"), "Scripts referenced by hooks (copied, never run).", customRootKey: key);
        s.Artifact("plugins", ArtifactKind.Plugins, "Plugins and marketplaces",
            [s.Combine(root, "plugins", "installed_plugins.json"), s.Combine(root, "plugins", "known_marketplaces.json")],
            "Installed plugin and marketplace references; plugins are reinstalled from their sources.",
            capability: RestoreCapability.ReinstallGuidance | RestoreCapability.DependencyInstall,
            excluded: ["Downloaded plugin and marketplace caches"], customRootKey: key);

        if (userMcp.Count > 0)
        {
            s.Artifact("user-mcp", ArtifactKind.McpConfiguration, "MCP servers (user scope)", stateFile,
                $"{AdapterScope.Count(userMcp.Count, "server", "servers")} from .claude.json. Only the mcpServers section is captured; the rest is account and session state.",
                Sensitivity.MayContainSecrets, capability: RestoreCapability.StructuredMerge, customRootKey: key);
        }

        s.Credential("credentials", "Sign-in (.credentials.json)", s.Combine(root, ".credentials.json"),
            "Claude sign-in. Excluded by default; sign in again on the new computer.", customRootKey: key);
    }
}

/// <summary>Claude Desktop: local MCP server configuration and extension inventory; other internal state is manual.</summary>
public sealed class ClaudeDesktopAdapter : ToolAdapter
{
    public override string Id => "claude-desktop";

    public override string DisplayName => "Claude Desktop";

    protected override string ToolId => "claude-desktop";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        // Classic installer location, then the packaged (MSIX) app's virtualized AppData.
        var candidates = new List<string> { s.Combine(s.Folders.RoamingAppData, "Claude") };
        foreach (var package in s.Children(s.Combine(s.Folders.LocalAppData, "Packages")).Where(p => p.IsDirectory && p.Name.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(s.Combine(package.FullPath, "LocalCache", "Roaming", "Claude"));
        }

        foreach (var (folder, index) in candidates.Where(s.Exists).Select((f, i) => (f, i)))
        {
            var suffix = index == 0 ? string.Empty : $"-{index}";
            var config = s.Combine(folder, "claude_desktop_config.json");
            var servers = ConfigReaders.McpServerNames(s.Fs, config, "mcpServers");
            s.ConfigurationItem("Claude Desktop configuration", folder, new Dictionary<string, string> { ["mcpServers"] = string.Join(", ", servers) });

            s.Artifact($"config{suffix}", ArtifactKind.McpConfiguration, index == 0 ? "MCP server configuration" : "MCP server configuration (packaged app data)", config,
                $"{AdapterScope.Count(servers.Count, "local MCP server", "local MCP servers")}.", Sensitivity.MayContainSecrets,
                capability: RestoreCapability.ExplicitFileRestore | RestoreCapability.StructuredMerge,
                excluded: ["Conversations, local storage, caches and logs (internal state)"]);

            var extensions = s.Children(s.Combine(folder, "Claude Extensions")).Where(e => e.IsDirectory).ToList();
            if (extensions.Count > 0)
            {
                s.Artifact($"extensions{suffix}", ArtifactKind.ExtensionInventory, "Desktop extensions (inventory)", s.Combine(folder, "Claude Extensions"),
                    $"{AdapterScope.Count(extensions.Count, "extension", "extensions")}, recorded for manual reinstallation.",
                    eligibility: BackupEligibility.InventoryOnly, capability: RestoreCapability.ReinstallGuidance);
            }
        }
    }
}

/// <summary>Gemini CLI (~/.gemini): settings incl. MCP servers, GEMINI.md, custom commands and extensions.</summary>
public sealed class GeminiCliAdapter : ToolAdapter
{
    public override string Id => "gemini-cli";

    public override string DisplayName => "Gemini CLI";

    protected override string ToolId => "gemini-cli";

    protected override void Discover(AdapterScope s, CancellationToken cancellationToken)
    {
        var (home, _) = s.ResolveRoot(s.Folders.UserProfile, "GEMINI_CLI_HOME");
        var root = s.Combine(home, ".gemini");
        if (!s.Exists(root))
        {
            return;
        }

        var servers = ConfigReaders.McpServerNames(s.Fs, s.Combine(root, "settings.json"), "mcpServers");
        s.ConfigurationItem("Gemini CLI configuration", root, new Dictionary<string, string> { ["mcpServers"] = string.Join(", ", servers) });

        s.Artifact("settings", ArtifactKind.Settings, "Settings", [s.Combine(root, "settings.json")],
            servers.Count == 0 ? "User settings." : $"User settings and {AdapterScope.Count(servers.Count, "MCP server", "MCP servers")}.",
            Sensitivity.MayContainSecrets, capability: RestoreCapability.ExplicitFileRestore | RestoreCapability.StructuredMerge,
            excluded: ["Chat checkpoints and temporary files (tmp/)", "Command history"]);
        s.Artifact("gemini-md", ArtifactKind.Instructions, "GEMINI.md", s.Combine(root, "GEMINI.md"), "Global instructions.");
        s.Artifact("commands", ArtifactKind.Commands, "Custom commands", s.Combine(root, "commands"), "TOML command definitions.");
        s.Artifact("extensions", ArtifactKind.Plugins, "Extensions", s.Combine(root, "extensions"), "Installed extensions and their manifests.");
        s.Credential("oauth", "Google sign-in (oauth_creds.json)", s.Combine(root, "oauth_creds.json"), "OAuth tokens. Sign in again on the new computer.", portable: false);
        s.Credential("accounts", "Account list (google_accounts.json)", s.Combine(root, "google_accounts.json"), "Cached account identities.", portable: false);
        s.Credential("mcp-oauth", "MCP OAuth tokens", s.Combine(root, "mcp-oauth-tokens.json"), "Per-server OAuth tokens.", portable: false);
    }
}
