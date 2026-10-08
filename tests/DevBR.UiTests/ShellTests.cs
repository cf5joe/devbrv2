using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace DevBR.UiTests;

/// <summary>
/// End-to-end UI checks against DevBR.exe started with <c>--machine sample</c> and an isolated data root.
/// </summary>
public sealed class ShellTests
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMinutes(3);

    [UiFact]
    public void Startup_does_not_run_discovery()
    {
        using var app = DevBRApp.Launch();

        Assert.NotNull(app.Page("Overview"));
        var discovery = app.GoTo("Discovery");

        var start = app.Button(discovery, "Start discovery");
        Assert.True(start.IsEnabled);
        Assert.Null(discovery.FindFirstDescendant(app.By.ByControlType(ControlType.Tab)));
        Assert.Null(discovery.FindFirstDescendant(app.By.ByName("Discovery in progress")));
        Assert.Null(discovery.FindFirstDescendant(app.By.ByName("Cancel")));
    }

    [UiFact]
    public void Sidebar_reaches_every_page_by_keyboard()
    {
        using var app = DevBRApp.Launch();
        Focus(app);

        // The sidebar has keyboard focus at startup; arrow keys move through the pages.
        app.WaitUntil(() => app.Navigation.Items[0].Properties.HasKeyboardFocus.ValueOrDefault, TimeSpan.FromSeconds(10), "sidebar focus");
        for (var i = 1; i < DevBRApp.PageNames.Length; i++)
        {
            Keyboard.Type(VirtualKeyShort.DOWN);
            var name = DevBRApp.PageNames[i];
            try
            {
                app.WaitFor(() => app.Page(name), TimeSpan.FromSeconds(10), $"the {name} page after pressing Down");
            }
            catch (TimeoutException ex)
            {
                var focus = app.Automation.FocusedElement();
                throw new TimeoutException($"{ex.Message} Focus: {focus?.ControlType} '{focus?.Name}' #{focus?.Properties.AutomationId.ValueOrDefault}, foreground: {app.Window.IsAvailable}", ex);
            }
            Assert.True(app.Navigation.Items[i].IsSelected);
        }

        // Ctrl+1..6 jump straight to a page from anywhere.
        for (var i = 0; i < DevBRApp.PageNames.Length; i++)
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1 + (ushort)i);
            var name = DevBRApp.PageNames[i];
            app.WaitFor(() => app.Page(name), TimeSpan.FromSeconds(10), $"the {name} page after Ctrl+{i + 1}");
        }

        // Tab leaves the sidebar (one tab stop) and lands on the page; Shift+Tab comes back: no keyboard trap.
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1);
        app.Navigation.Items[0].Focus();
        Keyboard.Type(VirtualKeyShort.TAB);
        app.WaitUntil(() => !IsInside(app.Automation.FocusedElement(), app.Navigation), TimeSpan.FromSeconds(5), "focus to leave the sidebar");
        var focused = app.Automation.FocusedElement();
        Assert.True(IsInside(focused, app.Page("Overview")!) || IsInside(focused, app.Window), "Tab should move into the window content");
        Assert.False(string.IsNullOrWhiteSpace(focused.Name), "the first control after the sidebar has a name");
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        app.WaitUntil(() => IsInside(app.Automation.FocusedElement(), app.Navigation), TimeSpan.FromSeconds(5), "Shift+Tab back to the sidebar");
    }

    [UiFact]
    public void Theme_presets_apply_from_settings()
    {
        using var app = DevBRApp.Launch();
        var settings = app.GoTo("Settings");
        var themes = app.Find(settings, app.By.ByControlType(ControlType.List).And(app.By.ByName("Theme")), "theme list").AsListBox();
        Assert.Equal(["Graphite", "Midnight", "Plum", "Light", "Follow Windows"], themes.Items.Select(i => i.Name));

        var backgrounds = new Dictionary<string, Color>();
        foreach (var name in new[] { "Midnight", "Plum", "Light", "Follow Windows", "Graphite" })
        {
            var item = themes.Items.First(i => i.Name == name);
            item.Select();
            app.WaitUntil(() => item.IsSelected, TimeSpan.FromSeconds(5), $"{name} to be selected");
            var stored = name.Replace(" ", string.Empty, StringComparison.Ordinal);
            app.WaitUntil(() => SettingsJson(app).Contains(stored, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(5), $"{name} to be saved");
            Thread.Sleep(300);
            backgrounds[name] = SampleBackground(app);
        }

        Assert.True(Luminance(backgrounds["Light"]) > 0.7, $"Light background {backgrounds["Light"]}");
        foreach (var dark in new[] { "Graphite", "Midnight", "Plum" })
        {
            Assert.True(Luminance(backgrounds[dark]) < 0.25, $"{dark} background {backgrounds[dark]}");
        }

        Assert.NotEqual(backgrounds["Graphite"], backgrounds["Midnight"]);
        Assert.NotEqual(backgrounds["Midnight"], backgrounds["Plum"]);
        Assert.Contains(backgrounds["Follow Windows"], new[] { backgrounds["Graphite"], backgrounds["Light"] });
    }

    [UiFact]
    public void Discovery_runs_to_completion_on_the_sample_machine()
    {
        using var app = DevBRApp.Launch();
        var page = RunDiscovery(app);

        var inventory = app.Find(page, app.By.ByControlType(ControlType.List).And(app.By.ByName("Inventory")), "inventory").AsListBox();
        Assert.NotEmpty(inventory.Items);
        Assert.Null(page.FindFirstDescendant(app.By.ByName("These results are incomplete")));
    }

    [UiFact]
    public void Every_page_has_meaningful_accessible_names()
    {
        using var app = DevBRApp.Launch();
        var problems = new List<string>();

        problems.AddRange(AccessibilityAudit.Run(app.Window, "Startup"));
        foreach (var name in DevBRApp.PageNames)
        {
            problems.AddRange(AccessibilityAudit.Run(app.GoTo(name), name + " (empty)"));
        }

        var discovery = RunDiscovery(app);
        problems.AddRange(AccessibilityAudit.Run(discovery, "Discovery > Inventory"));
        var inventory = app.Find(discovery, app.By.ByControlType(ControlType.List).And(app.By.ByName("Inventory")), "inventory").AsListBox();
        inventory.Items[0].Select();
        problems.AddRange(AccessibilityAudit.Run(discovery, "Discovery > Inventory with details"));
        foreach (var tab in discovery.FindAllDescendants(app.By.ByControlType(ControlType.TabItem)).Skip(1).Select(t => t.AsTabItem()))
        {
            tab.Select();
            Thread.Sleep(300);
            problems.AddRange(AccessibilityAudit.Run(discovery, $"Discovery > {tab.Name}"));
        }

        foreach (var name in DevBRApp.PageNames)
        {
            problems.AddRange(AccessibilityAudit.Run(app.GoTo(name), name + " (after discovery)"));
        }

        var backup = app.GoTo("Backup");
        foreach (var step in AdvanceBackupWizard(app, backup))
        {
            problems.AddRange(AccessibilityAudit.Run(backup, $"Backup > {step}"));
        }

        Assert.True(problems.Count == 0, $"{problems.Count} accessibility problem(s):\n" + string.Join("\n", problems.Distinct()));
    }

    [UiFact]
    public void Backup_wizard_reaches_the_review_step()
    {
        using var app = DevBRApp.Launch();
        RunDiscovery(app);
        var backup = app.GoTo("Backup");

        var steps = AdvanceBackupWizard(app, backup).ToList();

        Assert.Equal("Confirm the plan", steps[^1]);
        Assert.True(app.Button(backup, "Back up now").IsEnabled);
        Assert.NotNull(backup.FindFirstDescendant(app.By.ByName("Capture plan")));
    }

    [UiFact]
    public void Backup_then_restore_to_a_clean_target_with_accessible_results()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "ui-backups", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "sample.devbr");
        var problems = new List<string>();
        try
        {
            using (var app = DevBRApp.Launch())
            {
                RunDiscovery(app);
                var backup = app.GoTo("Backup");
                _ = AdvanceBackupWizard(app, backup, file).ToList();
                app.Button(backup, "Back up now").Invoke();
                app.WaitFor(() => backup.FindFirstDescendant(app.By.ByName("Start a new backup")), TimeSpan.FromMinutes(3), "the backup report");
                Assert.True(File.Exists(file));
                Assert.NotNull(backup.FindFirstDescendant(app.By.ByName("Backup report")));
                problems.AddRange(AccessibilityAudit.Run(backup, "Backup > report"));
            }

            using (var app = DevBRApp.Launch("clean", "--open-backup", file))
            {
                var restore = app.WaitFor(() => app.Page("Restore"), TimeSpan.FromSeconds(30), "the Restore page");
                app.WaitFor(() => restore.FindFirstDescendant(app.By.ByName("What this backup contains")), TimeSpan.FromMinutes(1), "the backup overview");
                problems.AddRange(AccessibilityAudit.Run(restore, "Restore > overview"));

                app.Button(restore, "Run preflight").Invoke();
                var approve = app.Button(restore, "Approve this plan");
                app.WaitUntil(() => approve.IsEnabled, TimeSpan.FromMinutes(2), "an approvable plan");
                problems.AddRange(AccessibilityAudit.Run(restore, "Restore > plan"));

                approve.Invoke();
                app.AnswerDialog("Yes", TimeSpan.FromSeconds(3));
                var restoreNow = app.Button(restore, "Restore now");
                app.WaitUntil(() => restoreNow.IsEnabled, TimeSpan.FromSeconds(30), "Restore now to be enabled");
                restoreNow.Invoke();
                Assert.Equal("Restore now?", app.AnswerDialog("Yes", TimeSpan.FromSeconds(15)));

                // Simulated administrator prompts, if any, are approved like a user would.
                AutomationElement recent;
                try
                {
                    recent = app.WaitFor(() =>
                    {
                        app.AnswerDialog("Yes", TimeSpan.FromMilliseconds(100));
                        var list = restore.FindFirstDescendant(app.By.ByControlType(ControlType.List).And(app.By.ByName("Recent restores")));
                        return list is not null && list.FindAllChildren().Length > 0 && restore.FindFirstDescendant(app.By.ByControlType(ControlType.List).And(app.By.ByName("Restore results"))) is not null ? list : null;
                    }, TimeSpan.FromMinutes(3), "the restore results and recent restores");
                }
                catch (TimeoutException ex)
                {
                    var texts = restore.FindAllDescendants(app.By.ByControlType(ControlType.Text)).Select(t => t.Name).Where(t => t.Length > 0);
                    throw new TimeoutException(ex.Message + " Page shows: " + string.Join(" | ", texts), ex);
                }

                Assert.DoesNotContain(recent.FindAllChildren(), item => item.Name.Contains('{', StringComparison.Ordinal));
                problems.AddRange(AccessibilityAudit.Run(restore, "Restore > results"));
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Assert.True(problems.Count == 0, $"{problems.Count} accessibility problem(s):\n" + string.Join("\n", problems.Distinct()));
    }

    /// <summary>Walks the wizard from step 1 to step 5 (confirm), yielding each step's heading.</summary>
    private static IEnumerable<string> AdvanceBackupWizard(DevBRApp app, AutomationElement backup, string? outputPath = null)
    {
        yield return "What will be backed up";
        var transitions = new (string Button, string Heading)[]
        {
            ("Next", "Add your own files and folders"),
            ("Review", "Review"),
            ("Continue", "Where and how to save it"),
            ("Review the plan", "Confirm the plan"),
        };

        foreach (var (button, heading) in transitions)
        {
            if (heading == "Confirm the plan" && outputPath is not null)
            {
                app.Find(backup, app.By.ByName("Backup file path"), "backup file path").AsTextBox().Text = outputPath;
            }

            if (heading == "Confirm the plan" && backup.FindFirstDescendant(app.By.ByName("Backup password")) is { IsOffscreen: false } password)
            {
                password.AsTextBox().Text = "correct horse battery staple";
                app.Find(backup, app.By.ByName("Confirm backup password"), "password confirmation").AsTextBox().Text = "correct horse battery staple";
            }

            var next = app.Button(backup, button);
            app.WaitUntil(() => next.IsEnabled, TimeSpan.FromSeconds(60), $"'{button}' to be enabled");
            next.Invoke();
            app.WaitFor(() => backup.FindFirstDescendant(app.By.ByControlType(ControlType.Text).And(app.By.ByName(heading))), TimeSpan.FromSeconds(60), $"step '{heading}'");
            yield return heading;
        }
    }

    private static AutomationElement RunDiscovery(DevBRApp app)
    {
        var page = app.GoTo("Discovery");
        app.Button(page, "Start discovery").Invoke();
        app.WaitFor(() => page.FindFirstDescendant(app.By.ByControlType(ControlType.Tab)), DiscoveryTimeout, "discovery results");
        app.WaitUntil(() => app.Button(page, "Start discovery").IsEnabled, TimeSpan.FromSeconds(30), "discovery to finish");
        return page;
    }

    private static void Focus(DevBRApp app)
    {
        app.Window.SetForeground();
        app.Window.Focus();
        Thread.Sleep(200);
    }

    private static bool IsInside(AutomationElement? element, AutomationElement container)
    {
        for (var current = element; current is not null; current = current.Parent)
        {
            if (current.Equals(container))
            {
                return true;
            }
        }

        return false;
    }

    private static string SettingsJson(DevBRApp app)
    {
        var path = Path.Combine(app.DataRoot, "settings.json");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>A pixel of page background just right of the sidebar.</summary>
    private static Color SampleBackground(DevBRApp app)
    {
        Focus(app);
        var nav = app.Navigation.BoundingRectangle;
        var window = app.Window.BoundingRectangle;
        using var capture = Capture.Rectangle(new Rectangle(nav.Right + 40, window.Top + (window.Height / 2), 1, 1));
        return capture.Bitmap.GetPixel(0, 0);
    }

    private static double Luminance(Color c) => ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255;
}
