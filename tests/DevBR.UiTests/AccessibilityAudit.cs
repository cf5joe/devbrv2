using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace DevBR.UiTests;

/// <summary>
/// A generic screen-reader audit of everything currently in the UI Automation tree under a root:
/// interactive controls need a real name, and nothing may announce a C# record dump, a type name, or a
/// bare icon-font glyph.
/// </summary>
public static partial class AccessibilityAudit
{
    private static readonly HashSet<ControlType> Interactive =
    [
        ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit, ControlType.Hyperlink,
        ControlType.List, ControlType.ListItem, ControlType.MenuItem, ControlType.RadioButton, ControlType.Slider,
        ControlType.Spinner, ControlType.Tab, ControlType.TabItem, ControlType.Tree, ControlType.TreeItem,
        ControlType.SplitButton, ControlType.DataGrid, ControlType.DataItem, ControlType.ProgressBar,
    ];

    public static IReadOnlyList<string> Run(AutomationElement root, string context)
    {
        var problems = new List<string>();
        Visit(root, context, problems, insideScrollBar: false);
        return problems;
    }

    private static void Visit(AutomationElement element, string path, List<string> problems, bool insideScrollBar)
    {
        foreach (var child in element.FindAllChildren())
        {
            ControlType type;
            string name;
            bool isControl;
            try
            {
                type = child.Properties.ControlType.ValueOrDefault;
                name = child.Properties.Name.ValueOrDefault ?? string.Empty;
                isControl = child.Properties.IsControlElement.ValueOrDefault;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                continue; // element went away while walking
            }

            var scrollBar = insideScrollBar || type == ControlType.ScrollBar;
            var where = $"{path} > {type}";
            var id = child.Properties.AutomationId.ValueOrDefault;
            var label = $"{where}{(string.IsNullOrEmpty(id) ? string.Empty : $"#{id}")} \"{Truncate(name)}\"";

            if (isControl)
            {
                if (RecordDump().IsMatch(name))
                {
                    problems.Add($"Record dump announced: {label}");
                }
                else if (TypeName().IsMatch(name))
                {
                    problems.Add($"Type name announced: {label}");
                }
                else if (name.Length > 0 && name.All(c => c is >= '\uE000' and <= '\uF8FF' || char.IsWhiteSpace(c)))
                {
                    problems.Add($"Icon glyph announced: {label}");
                }
                else if (!scrollBar && Interactive.Contains(type) && string.IsNullOrWhiteSpace(name))
                {
                    problems.Add($"Unnamed interactive element: {label}");
                }
            }

            Visit(child, string.IsNullOrWhiteSpace(name) ? where : $"{path} > {Truncate(name, 30)}", problems, scrollBar);
        }
    }

    private static string Truncate(string value, int max = 80) => value.Length <= max ? value : value[..max] + "…";

    // "RecentRestoreRow { Job = JournalJob { ... } }"
    [GeneratedRegex(@"\w\s*\{\s*\w+\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex RecordDump();

    // "DevBR.App.ViewModels.InventoryRow", "System.Collections.Generic.List`1[...]"
    [GeneratedRegex(@"^(DevBR|System|MS|Microsoft)(\.\w+)+(`\d+)?(\[.*\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeName();
}
