using System.Diagnostics;
using System.Reflection;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace DevBR.UiTests;

/// <summary>
/// One running DevBR.exe on a simulated machine with its own data root (DEVBR_DATA_ROOT), so tests never
/// read or write the real %LOCALAPPDATA%\DevBR. Disposing closes the window and deletes the data root.
/// </summary>
public sealed class DevBRApp : IDisposable
{
    public static readonly string[] PageNames = ["Overview", "Discovery", "Backup", "Restore", "Activity", "Settings"];

    private readonly Application _app;

    private DevBRApp(Application app, UIA3Automation automation, Window window, string dataRoot)
    {
        _app = app;
        Automation = automation;
        Window = window;
        DataRoot = dataRoot;
    }

    public UIA3Automation Automation { get; }

    public Window Window { get; }

    public string DataRoot { get; }

    public ConditionFactory By => Automation.ConditionFactory;

    public static DevBRApp Launch(string machine = "sample", params string[] extraArguments)
    {
        var dataRoot = Path.Combine(AppContext.BaseDirectory, "ui-state", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dataRoot);

        var start = new ProcessStartInfo(ExePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath)! };
        start.ArgumentList.Add("--machine");
        start.ArgumentList.Add(machine);
        foreach (var argument in extraArguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DEVBR_DATA_ROOT"] = dataRoot;

        var app = Application.Launch(start);
        var automation = new UIA3Automation();
        try
        {
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(60))
                ?? throw new InvalidOperationException("DevBR did not show its main window.");
            var session = new DevBRApp(app, automation, window, dataRoot);
            session.WaitFor(() => session.Navigation, TimeSpan.FromSeconds(30), "the sidebar");
            return session;
        }
        catch
        {
            Stop(app);
            automation.Dispose();
            throw;
        }
    }

    public static string ExePath { get; } = FindExe();

    public ListBox Navigation => Window.FindFirstDescendant(By.ByAutomationId("Navigation"))?.AsListBox()
        ?? throw new InvalidOperationException("Sidebar not found.");

    public AutomationElement? Page(string name) => Window.FindFirstDescendant(By.ByAutomationId(name + "Page"));

    public AutomationElement CurrentPage => PageNames.Select(Page).FirstOrDefault(p => p is not null)
        ?? throw new InvalidOperationException("No page is shown.");

    public AutomationElement GoTo(string page)
    {
        var item = Navigation.Items.First(i => i.Name == page);
        item.Select();
        return WaitFor(() => Page(page), TimeSpan.FromSeconds(10), $"the {page} page");
    }

    public Button Button(AutomationElement scope, string name)
        => WaitFor(() => scope.FindFirstDescendant(By.ByControlType(ControlType.Button).And(By.ByName(name)))?.AsButton(),
            TimeSpan.FromSeconds(10), $"button '{name}'");

    public AutomationElement Find(AutomationElement scope, ConditionBase condition, string what)
        => WaitFor(() => scope.FindFirstDescendant(condition), TimeSpan.FromSeconds(10), what);

    /// <summary>Answers the next modal message box (if one appears within <paramref name="timeout"/>) with <paramref name="button"/>.</summary>
    public string? AnswerDialog(string button, TimeSpan timeout)
    {
        var dialog = Retry.WhileNull(() => Window.ModalWindows.FirstOrDefault(), timeout, TimeSpan.FromMilliseconds(200)).Result;
        if (dialog is null)
        {
            return null;
        }

        var title = dialog.Title;
        WaitFor(() => dialog.FindFirstDescendant(By.ByControlType(ControlType.Button).And(By.ByName(button)))?.AsButton(), TimeSpan.FromSeconds(5), $"'{button}' in '{title}'").Invoke();
        WaitUntil(() => !Window.ModalWindows.Any(w => w.Title == title), TimeSpan.FromSeconds(10), $"'{title}' to close");
        return title;
    }

    public T WaitFor<T>(Func<T?> find, TimeSpan timeout, string what)
        where T : class
    {
        var result = Retry.WhileNull(() =>
        {
            try
            {
                return find();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return null;
            }
        }, timeout, TimeSpan.FromMilliseconds(200));
        return result.Result ?? throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}.");
    }

    public void WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
    {
        if (!Retry.WhileFalse(() =>
            {
                try
                {
                    return condition();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    return false;
                }
            }, timeout, TimeSpan.FromMilliseconds(200)).Result)
        {
            throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0} s waiting for {what}.");
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_app.HasExited)
            {
                Window.Patterns.Window.PatternOrDefault?.Close();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }

        Stop(_app);
        Automation.Dispose();
        _app.Dispose();
        DeleteDataRoot();
    }

    private static void Stop(Application app)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(app.ProcessId);
        }
        catch (ArgumentException)
        {
            return; // already exited
        }

        using (process)
        {
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
    }

    private void DeleteDataRoot()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(DataRoot))
                {
                    Directory.Delete(DataRoot, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static string FindExe()
    {
        if (Environment.GetEnvironmentVariable("DEVBR_EXE") is { Length: > 0 } explicitPath)
        {
            return explicitPath;
        }

        var configuration = typeof(DevBRApp).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "DevBR.Configuration")?.Value ?? "Debug";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DevBR.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new FileNotFoundException("Could not find the repository root (DevBR.slnx). Set DEVBR_EXE to the built DevBR.exe.");
        }

        var bin = Path.Combine(directory.FullName, "src", "DevBR.App", "bin", "x64", configuration);
        return Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, "DevBR.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new FileNotFoundException($"DevBR.exe not found under {bin}. Build the solution first.")
            : throw new FileNotFoundException($"{bin} does not exist. Build the solution first.");
    }
}
