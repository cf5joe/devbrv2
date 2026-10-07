using CommunityToolkit.Mvvm.Input;
using DevBR.Infrastructure;

namespace DevBR.App.ViewModels;

public sealed record ScopeItem(string Glyph, string Title, string Description);

public sealed partial class DiscoveryViewModel : PageViewModel
{
    public override string Title => "Discovery";

    public override string Glyph => "";

    public bool IsElevated => BuildInfo.IsElevated;

    public string ElevationTitle => IsElevated ? "Running as administrator" : "Running as a standard user";

    public string ElevationDetail => IsElevated
        ? "Protected inventory sources can be read directly."
        : "Locations that need administrator rights are listed as inaccessible in the coverage report rather than skipped silently.";

    public IReadOnlyList<ScopeItem> Scope { get; } =
    [
        new("", "Installed software", "Uninstall records (64- and 32-bit, user and machine), Store packages, App Paths, Start menu, and package-manager metadata."),
        new("", "All fixed local drives", "Recognized tools, repositories, configuration, plugins, skills and workflow files. Junctions, cloud placeholders and other users' profiles are not entered."),
        new("", "Environment", "User and machine variables, kept separate, with raw values and registry types."),
        new("", "Developer tools", "VS Code, Copilot, Codex, Claude, Cursor, Gemini CLI, Git/GitHub CLI, PowerShell, Windows Terminal, WSL and Docker."),
    ];

    /// <summary>The discovery engine is delivered in Phase 2; the command stays disabled until then.</summary>
    [RelayCommand(CanExecute = nameof(CanStartDiscovery))]
    private void StartDiscovery()
    {
    }

    private static bool CanStartDiscovery() => false;
}
