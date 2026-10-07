using System.Text.RegularExpressions;
using DevBR.Domain;

namespace DevBR.Discovery.Catalog;

/// <param name="Executables">File names that identify the tool when found on disk or PATH. Matching never runs them.</param>
/// <param name="UninstallNamePattern">Matches Add/Remove Programs display names.</param>
/// <param name="StorePackageNames">MSIX package names (without publisher hash).</param>
/// <param name="NpmPackages">Global npm package names that provide the tool.</param>
/// <param name="AdapterId">The migration adapter that handles this tool's configuration, if any.</param>
public sealed record KnownTool(
    string Id,
    string DisplayName,
    InventoryCategory Category,
    string? AdapterId,
    string[] Executables,
    string? UninstallNamePattern = null,
    string[]? StorePackageNames = null,
    string[]? NpmPackages = null,
    string? LocationPattern = null)
{
    private readonly Regex? _uninstall = UninstallNamePattern is null ? null : new Regex(UninstallNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly Regex? _location = LocationPattern is null ? null : new Regex(LocationPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Disambiguates tools sharing an executable name (claude.exe) by where the file lives.</summary>
    public bool MatchesLocation(string? directory) => _location is null || (directory is not null && _location.IsMatch(directory));

    public bool MatchesUninstallName(string displayName) => _uninstall?.IsMatch(displayName) ?? false;
}

public static class KnownTools
{
    public static IReadOnlyList<KnownTool> All { get; } =
    [
        new("vscode", "Visual Studio Code", InventoryCategory.DeveloperTool, "vscode", ["Code.exe"], @"^Microsoft Visual Studio Code( \(User\))?$"),
        new("vscode-insiders", "Visual Studio Code - Insiders", InventoryCategory.DeveloperTool, "vscode-insiders", ["Code - Insiders.exe"], @"^Microsoft Visual Studio Code - Insiders"),
        new("cursor", "Cursor", InventoryCategory.DeveloperTool, "cursor", ["Cursor.exe"], @"^Cursor(\s|$)"),
        // Both ship "claude.exe": Claude Code lives in ~/.local/bin, npm or a claude-code folder; the desktop app elsewhere.
        new("claude-code", "Claude Code", InventoryCategory.DeveloperTool, "claude-code", ["claude.exe"], @"^Claude Code", null, ["@anthropic-ai/claude-code"],
            @"\\\.local\\bin$|\\npm(\\|$)|claude-code|\\\.claude\\"),
        new("claude-desktop", "Claude Desktop", InventoryCategory.DeveloperTool, "claude-desktop", ["Claude.exe"], @"^Claude$", ["Claude"]),
        new("codex", "Codex CLI", InventoryCategory.DeveloperTool, "codex", ["codex.exe"], @"^Codex", null, ["@openai/codex"]),
        new("gemini-cli", "Gemini CLI", InventoryCategory.DeveloperTool, "gemini-cli", ["gemini.exe"], null, null, ["@google/gemini-cli"]),
        new("copilot-cli", "GitHub Copilot CLI", InventoryCategory.DeveloperTool, "copilot", ["copilot.exe"], @"^GitHub Copilot CLI", null, ["@github/copilot"]),
        new("git", "Git", InventoryCategory.DeveloperTool, "git", ["git.exe", "git-bash.exe"], @"^Git( version [\d.]+)?$|^Git for Windows"),
        new("gh", "GitHub CLI", InventoryCategory.DeveloperTool, "gh", ["gh.exe"], @"^GitHub CLI"),
        new("pwsh", "PowerShell 7", InventoryCategory.Runtime, "powershell", ["pwsh.exe"], @"^PowerShell 7"),
        new("windows-terminal", "Windows Terminal", InventoryCategory.DeveloperTool, "windows-terminal", ["WindowsTerminal.exe", "wt.exe"], null, ["Microsoft.WindowsTerminal", "Microsoft.WindowsTerminalPreview"]),
        new("wsl", "Windows Subsystem for Linux", InventoryCategory.Runtime, "wsl", [], @"^Windows Subsystem for Linux", ["MicrosoftCorporationII.WindowsSubsystemForLinux"]),
        new("docker-desktop", "Docker Desktop", InventoryCategory.DeveloperTool, "docker", ["Docker Desktop.exe"], @"^Docker Desktop"),
        new("node", "Node.js", InventoryCategory.Runtime, null, ["node.exe"], @"^Node\.js"),
        new("python", "Python", InventoryCategory.Runtime, null, ["python.exe"], @"^Python \d+\.\d+(\.\d+)? \((64|32)-bit\)$|^Python \d+\.\d+\.\d+ \("),
        new("dotnet", ".NET", InventoryCategory.Runtime, null, ["dotnet.exe"], @"^Microsoft \.NET SDK"),
        new("java", "Java", InventoryCategory.Runtime, null, ["java.exe"], @"(Java|JDK|OpenJDK).*\d"),
        new("go", "Go", InventoryCategory.Runtime, null, ["go.exe"], @"^Go Programming Language"),
        new("rust", "Rust (rustup)", InventoryCategory.Runtime, null, ["rustup.exe", "cargo.exe"], @"^Rust"),
        new("winget", "WinGet", InventoryCategory.PackageManager, null, ["winget.exe"], null, ["Microsoft.DesktopAppInstaller"]),
        new("chocolatey", "Chocolatey", InventoryCategory.PackageManager, null, ["choco.exe"], @"^Chocolatey"),
        new("scoop", "Scoop", InventoryCategory.PackageManager, null, []),
        new("uv", "uv", InventoryCategory.PackageManager, null, ["uv.exe"]),
        new("pipx", "pipx", InventoryCategory.PackageManager, null, ["pipx.exe"]),
    ];

    private static readonly ILookup<string, KnownTool> ByExecutable = All
        .SelectMany(t => t.Executables.Select(e => (Exe: e, Tool: t)))
        .ToLookup(x => x.Exe, x => x.Tool, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> ExecutableNames { get; } = new HashSet<string>(ByExecutable.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);

    public static KnownTool? Get(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// The tool an executable belongs to. Tools sharing a file name are told apart by
    /// <paramref name="directory"/>; a tool without a location pattern is the fallback.
    /// </summary>
    public static KnownTool? ByExecutableName(string fileName, string? directory = null)
    {
        var candidates = ByExecutable[fileName].ToList();
        return candidates.FirstOrDefault(t => t.LocationPattern is not null && t.MatchesLocation(directory))
            ?? candidates.FirstOrDefault(t => t.LocationPattern is null);
    }

    public static KnownTool? ByUninstallName(string displayName) => All.FirstOrDefault(t => t.MatchesUninstallName(displayName));

    public static KnownTool? ByStorePackage(string packageName)
        => All.FirstOrDefault(t => t.StorePackageNames?.Any(n => string.Equals(n, packageName, StringComparison.OrdinalIgnoreCase)) == true);

    public static KnownTool? ByNpmPackage(string packageName)
        => All.FirstOrDefault(t => t.NpmPackages?.Any(n => string.Equals(n, packageName, StringComparison.OrdinalIgnoreCase)) == true);
}
