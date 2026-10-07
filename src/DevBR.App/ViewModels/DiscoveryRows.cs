using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DevBR.App.Services;
using DevBR.Domain;

namespace DevBR.App.ViewModels;

public sealed record LabeledValue(string Label, string Value);

public sealed record CountChip(string Label, int Count)
{
    public string Text => $"{Label} {Count.ToString("N0", CultureInfo.CurrentCulture)}";
}

/// <summary>One inventory record as shown in the list and details panel.</summary>
public sealed class InventoryRow(InventoryItem item, IReadOnlyList<MigrationArtifact> related)
{
    public InventoryItem Item { get; } = item;

    public string Name => Item.Name;

    public string Version => Item.Version ?? "—";

    public string Category => DiscoveryText.Category(Item.Category);

    public string Scope => DiscoveryText.Scope(Item.Scope);

    /// <summary>Keeps "detected" and "confirmed installed" visibly different.</summary>
    public string Status => Item.Status == DetectionStatus.ConfirmedInstalled ? "Confirmed installed" : "Detected";

    public bool IsConfirmed => Item.Status == DetectionStatus.ConfirmedInstalled;

    public string Confidence => Item.Confidence.ToString();

    /// <summary>"Developer tool · C:Program FilesGit" — category and primary location under the name.</summary>
    public string Subtitle => PrimaryLocation.Length == 0 ? Category : $"{Category} · {PrimaryLocation}";

    public string StatusShort => IsConfirmed ? "Confirmed" : "Detected";

    public string PrimaryLocation => Item.Locations.Count > 0 ? Item.Locations[0] : string.Empty;

    public string? Publisher => Item.Publisher;

    public IReadOnlyList<string> Locations => Item.Locations;

    public IReadOnlyList<LabeledValue> Evidence { get; } =
        [.. item.Evidence.Select(e => new LabeledValue(e.Source, e.Path is null ? e.Detail : $"{e.Detail}\n{e.Path}"))];

    public IReadOnlyList<LabeledValue> Properties { get; } =
        [.. (item.Properties ?? new Dictionary<string, string>()).Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => new LabeledValue(DiscoveryText.Humanize(p.Key), p.Value))];

    public string MigrationSupport => related.Count switch
    {
        0 when Item.Category is InventoryCategory.Application or InventoryCategory.DeveloperTool or InventoryCategory.Runtime or InventoryCategory.PackageManager or InventoryCategory.Package or InventoryCategory.Extension
            => "Inventory only: recorded with reinstall guidance. Program files are never copied.",
        0 => "Recorded in the inventory.",
        _ => $"Backup content available: {string.Join(", ", related.Select(a => a.DisplayName))}.",
    };

    public bool Matches(string search)
        => string.IsNullOrWhiteSpace(search)
           || Item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
           || (Item.Version?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
           || (Item.Publisher?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
           || Item.Locations.Any(l => l.Contains(search, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A selectable backup artifact. Selection changes are persisted through the catalog session.</summary>
public sealed partial class ArtifactRow : ObservableObject
{
    private readonly CatalogSession _session;
    private bool _syncing;

    public ArtifactRow(MigrationArtifact artifact, CatalogSession session)
    {
        Artifact = artifact;
        _session = session;
        Sync();
    }

    public MigrationArtifact Artifact { get; }

    public string Name => Artifact.DisplayName;

    public string Owner => DiscoveryText.Owner(Artifact.OwnerToolId);

    public string Kind => DiscoveryText.Humanize(Artifact.Kind.ToString());

    public string? Description => Artifact.Description;

    public string Size => Artifact.EstimatedBytes is { } bytes ? Formatting.Bytes(bytes) : string.Empty;

    public bool CanSelect => CatalogSession.CanSelect(Artifact);

    public bool IsDefault => Artifact.SelectedByDefault;

    public string? Excluded => Artifact.ExcludedContent is { Count: > 0 } excluded ? "Not included: " + string.Join("; ", excluded) : null;

    /// <summary>Text badge for sensitivity; never conveyed by color alone.</summary>
    public string? SensitivityLabel => Artifact.Sensitivity switch
    {
        Sensitivity.Credential => "Credential",
        Sensitivity.ContainsRecognizedSecrets => "Contains secrets",
        Sensitivity.MayContainSecrets => "May contain secrets",
        _ => null,
    };

    public bool IsSensitive => Artifact.Sensitivity is Sensitivity.Credential or Sensitivity.ContainsRecognizedSecrets;

    public string? EligibilityLabel => Artifact.Eligibility switch
    {
        BackupEligibility.InventoryOnly => "Inventory only",
        BackupEligibility.ExcludedByDefault => "Excluded by default",
        BackupEligibility.Blocked => "Not available",
        _ => null,
    };

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public void Sync()
    {
        _syncing = true;
        IsSelected = _session.IsSelected(Artifact);
        _syncing = false;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_syncing)
        {
            _ = _session.SetSelectedAsync(Artifact, value, CancellationToken.None);
        }
    }
}

public sealed class ArtifactGroup(string owner, IReadOnlyList<ArtifactRow> rows)
{
    public string Owner { get; } = owner;

    public IReadOnlyList<ArtifactRow> Rows { get; } = rows;
}

public sealed class CoverageRow(DiscoveryCoverage coverage)
{
    public string Source => coverage.Volume is null ? coverage.Source : $"{coverage.Source} — {coverage.Volume}";

    public string Counts
    {
        get
        {
            var parts = new List<string>();
            if (coverage.ScannedDirectories > 0)
            {
                parts.Add(Formatting.Count(coverage.ScannedDirectories, "folder", "folders"));
            }

            parts.Add(Formatting.Count(coverage.ScannedFiles, "entry", "entries"));
            if (coverage.EndedAt is { } ended)
            {
                parts.Add($"{(ended - coverage.StartedAt).TotalSeconds:0.0} s");
            }

            return string.Join(" · ", parts);
        }
    }

    public IReadOnlyList<LabeledValue> Excluded { get; } = [.. coverage.Exclusions.Select(e => new LabeledValue(e.Reason, e.Path))];

    public IReadOnlyList<LabeledValue> Inaccessible { get; } = [.. coverage.Inaccessible.Select(i => new LabeledValue(i.Reason, i.Path))];

    public IReadOnlyList<string> Errors { get; } = coverage.Errors;

    public bool HasDetails => Excluded.Count + Inaccessible.Count + Errors.Count > 0;

    public string Summary => $"{Formatting.Count(Excluded.Count, "skipped scope", "skipped scopes")}, {Formatting.Count(Inaccessible.Count, "inaccessible location", "inaccessible locations")}{(Errors.Count > 0 ? $", {Formatting.Count(Errors.Count, "error", "errors")}" : string.Empty)}";
}

public static class DiscoveryText
{
    public static string Category(InventoryCategory category) => category switch
    {
        InventoryCategory.DeveloperTool => "Developer tool",
        InventoryCategory.PackageManager => "Package manager",
        InventoryCategory.AiConfiguration => "AI configuration",
        InventoryCategory.EnvironmentVariable => "Environment variable",
        InventoryCategory.SystemFact => "System",
        InventoryCategory.CustomContent => "Custom content",
        _ => category.ToString(),
    };

    public static string Scope(InstallScope scope) => scope switch
    {
        InstallScope.User => "User",
        InstallScope.Machine => "All users",
        InstallScope.Portable => "Portable",
        InstallScope.Store => "Store",
        _ => "—",
    };

    public static string Owner(string toolId) => toolId switch
    {
        "devbr" => "Inventory",
        "windows" => "Windows environment",
        "vscode" => "VS Code",
        "vscode-insiders" => "VS Code Insiders",
        "cursor" => "Cursor",
        "copilot-cli" => "GitHub Copilot",
        "codex" => "Codex",
        "claude-code" => "Claude Code",
        "claude-desktop" => "Claude Desktop",
        "gemini-cli" => "Gemini CLI",
        "git" => "Git",
        "gh" => "GitHub CLI",
        "pwsh" => "PowerShell",
        "windows-terminal" => "Windows Terminal",
        "wsl" => "WSL",
        "docker-desktop" => "Docker",
        _ => toolId,
    };

    /// <summary>"objectAlternates" → "Object alternates".</summary>
    public static string Humanize(string key)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in key)
        {
            if (char.IsUpper(c) && builder.Length > 0)
            {
                builder.Append(' ');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(builder.Length == 0 ? char.ToUpperInvariant(c) : c);
            }
        }

        return builder.ToString().Replace("Mcp", "MCP", StringComparison.Ordinal).Replace(" mcp", " MCP", StringComparison.Ordinal);
    }
}

public static class DiscoveryLabels
{
    public static System.Windows.Data.IValueConverter StartLabel { get; } = new StartLabelConverter();

    private sealed class StartLabelConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? "Run discovery again" : "Start discovery";

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
