using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DevBR.Restore;

public enum McpServerKind
{
    Remote,
    NodePackage,
    PythonPackage,
    Container,
    LocalExecutable,
    Unsupported,
}

/// <param name="RequiredToolId">The runtime that must be installed for the server to start, if any.</param>
public sealed record McpServerInfo(string ArtifactId, string File, string Name, McpServerKind Kind, string? RequiredToolId, string Detail);

/// <summary>
/// Classifies MCP server definitions by what they need to run. Nothing is executed, and no command is
/// ever derived from these strings: a server is only mapped to one of a few known runtimes.
/// </summary>
public static class McpAnalyzer
{
    public static IEnumerable<McpServerInfo> Analyze(string artifactId, string file, JsonNode? document)
    {
        if (document is not JsonObject root)
        {
            yield break;
        }

        foreach (var container in new[] { "mcpServers", "servers" })
        {
            if (root[container] is not JsonObject servers)
            {
                continue;
            }

            foreach (var (name, definition) in servers)
            {
                yield return Classify(artifactId, file, name, definition as JsonObject);
            }
        }
    }

    private static McpServerInfo Classify(string artifactId, string file, string name, JsonObject? server)
    {
        if (server is null)
        {
            return new(artifactId, file, name, McpServerKind.Unsupported, null, "The definition is not an object.");
        }

        var type = (string?)server["type"];
        if (server["url"] is not null || server["httpUrl"] is not null || type is "http" or "sse" or "streamable-http")
        {
            return new(artifactId, file, name, McpServerKind.Remote, null, "Remote endpoint; nothing to install. Sign in again if it needs authentication.");
        }

        var command = (string?)server["command"];
        if (string.IsNullOrWhiteSpace(command))
        {
            return new(artifactId, file, name, McpServerKind.Unsupported, null, "No command or URL; restored as written, review it manually.");
        }

        if (command.Contains('\\', StringComparison.Ordinal) || command.Contains('/', StringComparison.Ordinal))
        {
            return new(artifactId, file, name, McpServerKind.LocalExecutable, null, $"Runs a local program ({command}); it must exist at that path on this computer.");
        }

        var executable = Path.GetFileNameWithoutExtension(command).ToLowerInvariant();
        return executable switch
        {
            "npx" or "npm" or "node" or "pnpm" or "yarn" => new(artifactId, file, name, McpServerKind.NodePackage, "node", "Starts through Node.js (npx); needs Node.js."),
            "uvx" or "uv" => new(artifactId, file, name, McpServerKind.PythonPackage, "uv", "Starts through uv (uvx); needs uv."),
            "python" or "python3" or "py" or "pipx" => new(artifactId, file, name, McpServerKind.PythonPackage, "python", "Starts through Python; needs Python."),
            "docker" or "podman" => new(artifactId, file, name, McpServerKind.Container, "docker-desktop", "Runs in a container; needs Docker. Images are pulled on first start."),
            _ => new(artifactId, file, name, McpServerKind.Unsupported, null, $"Uses '{command}', which DevBR does not recognize. It is restored as written; make sure the command exists."),
        };
    }
}

public enum RecipeKind
{
    WinGet,
    EditorExtension,
}

/// <summary>
/// An installation DevBR may perform after approval. Built only from validated identifiers in a fixed
/// catalog, never from strings found in a backup.
/// </summary>
public sealed record PackageRecipe(
    string Id,
    RecipeKind Kind,
    string DisplayName,
    string PackageId,
    string? Version,
    string Source,
    bool RequiresElevation)
{
    /// <summary>What will run, shown verbatim in the preview.</summary>
    public string Preview => Kind switch
    {
        RecipeKind.WinGet => $"winget install --id {PackageId} --exact --source winget{(Version is null ? string.Empty : $" --version {Version}")}",
        _ => $"{Source} --install-extension {PackageId}{(Version is null ? string.Empty : "@" + Version)}",
    };
}

public static partial class PackageRecipes
{
    /// <summary>Runtimes DevBR can install through WinGet when the target allows it.</summary>
    private static readonly Dictionary<string, (string Name, string WinGetId, bool Elevated)> Runtimes = new(StringComparer.Ordinal)
    {
        ["node"] = ("Node.js LTS", "OpenJS.NodeJS.LTS", true),
        ["uv"] = ("uv", "astral-sh.uv", false),
        ["python"] = ("Python 3.12", "Python.Python.3.12", false),
        ["docker-desktop"] = ("Docker Desktop", "Docker.DockerDesktop", true),
        ["git"] = ("Git", "Git.Git", true),
    };

    /// <summary>Install hints for host applications, which the user installs themselves.</summary>
    private static readonly Dictionary<string, string> HostHints = new(StringComparer.Ordinal)
    {
        ["vscode"] = "winget install Microsoft.VisualStudioCode, or download it from code.visualstudio.com",
        ["vscode-insiders"] = "winget install Microsoft.VisualStudioCode.Insiders",
        ["cursor"] = "download it from cursor.com",
        ["claude-desktop"] = "download it from claude.ai/download",
        ["claude-code"] = "follow the Claude Code installation instructions at docs.claude.com",
        ["codex"] = "npm install -g @openai/codex",
        ["gemini-cli"] = "npm install -g @google/gemini-cli",
        ["copilot-cli"] = "npm install -g @github/copilot",
        ["git"] = "winget install Git.Git",
        ["gh"] = "winget install GitHub.cli",
        ["pwsh"] = "winget install Microsoft.PowerShell",
        ["windows-terminal"] = "winget install Microsoft.WindowsTerminal, or install it from the Microsoft Store",
        ["docker-desktop"] = "winget install Docker.DockerDesktop",
        ["wsl"] = "wsl --install",
    };

    public static PackageRecipe? Runtime(string toolId)
        => Runtimes.TryGetValue(toolId, out var r) ? new PackageRecipe($"winget:{r.WinGetId}", RecipeKind.WinGet, r.Name, r.WinGetId, null, "winget (community repository)", r.Elevated) : null;

    public static string HostHint(string toolId) => HostHints.GetValueOrDefault(toolId, "install it from its publisher");

    /// <summary>An editor extension recipe, or null when the identifier or version is not well-formed.</summary>
    public static PackageRecipe? Extension(string editorToolId, string extensionId, string? version)
    {
        var cli = editorToolId switch
        {
            "vscode" => "code",
            "vscode-insiders" => "code-insiders",
            "cursor" => "cursor",
            _ => null,
        };

        if (cli is null || !ExtensionId().IsMatch(extensionId) || (version is not null && !SemVer().IsMatch(version)))
        {
            return null;
        }

        return new PackageRecipe($"ext:{editorToolId}:{extensionId.ToLowerInvariant()}", RecipeKind.EditorExtension, extensionId, extensionId, version, cli, false);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-]{0,62}\.[A-Za-z0-9][A-Za-z0-9\-]{0,126}$")]
    private static partial Regex ExtensionId();

    [GeneratedRegex(@"^\d{1,9}\.\d{1,9}\.\d{1,9}([\-+][0-9A-Za-z.\-]{1,64})?$")]
    private static partial Regex SemVer();
}
