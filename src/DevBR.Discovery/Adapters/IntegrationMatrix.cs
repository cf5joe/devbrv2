using System.Text;

namespace DevBR.Discovery.Adapters;

/// <summary>Renders the supported integration matrix (docs/INTEGRATIONS.md) from the adapters' support descriptors.</summary>
public static class IntegrationMatrix
{
    public const string BeginMarker = "<!-- BEGIN GENERATED: integration matrix (IntegrationMatrix.Render) -->";
    public const string EndMarker = "<!-- END GENERATED: integration matrix -->";

    private static readonly (AdapterCapabilities Flag, string Name)[] CapabilityNames =
    [
        (AdapterCapabilities.Inventory, "inventory"),
        (AdapterCapabilities.Capture, "capture"),
        (AdapterCapabilities.StructuredMerge, "structured merge"),
        (AdapterCapabilities.PathRewrite, "path rewrite"),
        (AdapterCapabilities.DependencyRecipes, "dependency recipes"),
    ];

    public static string Render(IEnumerable<IToolAdapter> adapters)
    {
        var list = adapters.ToList();
        var text = new StringBuilder();
        text.Append(BeginMarker).Append('\n').Append('\n');
        text.Append("| Adapter | Host (version source) | Verified versions | Capabilities | Unknown or unverified version |\n");
        text.Append("|---|---|---|---|---|\n");
        foreach (var adapter in list)
        {
            var s = adapter.Support;
            text.Append($"| {adapter.DisplayName} (`{adapter.Id}`) | {s.HostName} (`{s.HostToolId}`) | {s.VersionText} | {Capabilities(s.Capabilities)} | {s.Fallback} |\n");
        }

        foreach (var adapter in list)
        {
            var s = adapter.Support;
            text.Append('\n').Append($"### {adapter.DisplayName} (`{adapter.Id}`)\n\n");
            text.Append("Locations, in precedence order:\n\n");
            foreach (var location in s.Locations)
            {
                text.Append($"- `{location}`\n");
            }

            text.Append("\nPrerequisite rules:\n\n");
            foreach (var prerequisite in s.Prerequisites)
            {
                text.Append($"- {prerequisite}\n");
            }
        }

        text.Append('\n').Append(EndMarker);
        return text.ToString();
    }

    public static string Capabilities(AdapterCapabilities capabilities)
        => string.Join(", ", CapabilityNames.Where(c => capabilities.HasFlag(c.Flag)).Select(c => c.Name));
}
