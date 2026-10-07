using DevBR.Application.Archive;
using DevBR.Application.Machine;
using DevBR.Archive;
using DevBR.Backup;
using DevBR.Discovery;
using DevBR.Domain;
using DevBR.Restore;
using DevBR.Simulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBR.Tests.Restore;

/// <summary>
/// One backup of the simulated workstation (a realistic mix of settings, MCP configuration, environment,
/// extensions and repositories), shared by all restore-planning tests.
/// </summary>
public sealed class RestoreFixture : IAsyncLifetime
{
    public TempDirectory Temp { get; } = new();

    public SevenZipArchiveService Archive { get; } = new(Loggers.For<SevenZipArchiveService>());

    public BackupOverview Overview { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var source = SampleMachines.DeveloperWorkstation(Temp.Combine("source"));
        var snapshot = await new DiscoveryEngine(DiscoveryEngine.DefaultProviders(), NullLogger<DiscoveryEngine>.Instance)
            .RunAsync(source, DiscoveryOptions.Default, null, CancellationToken.None);

        string[] ids =
        [
            DiscoveryEngine.InventoryArtifactId, "environment:machine", "environment:user",
            "vscode:default:settings", "vscode:default:mcp", "vscode:extensions", "vscode:profile--5a2f1c:settings",
            "cursor:settings", "cursor:mcp", "copilot:mcp", "claude-code:settings", "claude-code:user-mcp", "claude-desktop:config",
            "codex:config", "git:config", "gh:config", "windows-terminal:stable:settings",
        ];
        var selected = snapshot.Artifacts.Where(a => ids.Contains(a.Id) || (a.Kind == ArtifactKind.Repository && a.DisplayName.Split(' ')[0] is "webapp" or "api" or "api-hotfix")).ToList();

        var plan = new BackupPlanner().Plan(new BackupPlanRequest(source, snapshot, selected, [], DefaultExclusions.All, [Temp.Path]), null, CancellationToken.None);
        var result = await new BackupRunner(Archive, NullLogger<BackupRunner>.Instance).RunAsync(plan,
            new BackupRunOptions(Temp.Combine("alice.devbr"), Temp.Combine("scratch"), CompressionPreset.Fast, new global::DevBR.Application.SecretText("restore-tests-password"), false),
            null, CancellationToken.None);
        if (!result.Verified)
        {
            throw new InvalidOperationException($"Fixture backup failed: {result.Message}");
        }

        Overview = await new BackupReader(Archive).OpenAsync(result.OutputPath!, new global::DevBR.Application.SecretText("restore-tests-password"), Temp.Combine("scratch"), CancellationToken.None);
    }

    public global::DevBR.Application.SecretText Password { get; } = new("restore-tests-password");

    /// <summary>A new target computer. The defaults match <see cref="SampleMachines.CleanTarget"/>.</summary>
    public SimulatedMachine Target(string name, Action<SimulatedMachineBuilder>? customize = null)
    {
        var b = new SimulatedMachineBuilder(SampleMachines.TargetUser, "NEW-LAPTOP");
        var local = b.Folders.LocalAppData;
        b.UninstallEntry(RegistryHive.LocalMachine, RegistryView.Registry64, "Git_is1", "Git", "2.56.0", "The Git Development Community", @"C:\Program Files\Git\")
         .Executable(@"C:\Program Files\Git\cmd\git.exe", "2.56.0");
        b.UninstallEntry(RegistryHive.CurrentUser, RegistryView.Registry64, "{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1", "Microsoft Visual Studio Code (User)", "1.106.0", "Microsoft Corporation",
            $@"{local}\Programs\Microsoft VS Code\")
         .Executable($@"{local}\Programs\Microsoft VS Code\Code.exe", "1.106.0")
         .Directory($@"{local}\Programs\Microsoft VS Code\bin");
        b.StorePackage("Microsoft.WindowsTerminal", "8wekyb3d8bbwe", "Windows Terminal", "1.24.12741.0")
         .StorePackage("Microsoft.DesktopAppInstaller", "8wekyb3d8bbwe", "App Installer", "1.27.0.0");
        b.Environment(RegistryHive.LocalMachine, "Path", @"%SystemRoot%\system32;%SystemRoot%;C:\Program Files\Git\cmd", expand: true)
         .Environment(RegistryHive.CurrentUser, "Path", @"%LOCALAPPDATA%\Programs\Microsoft VS Code\bin", expand: true);
        b.File($@"{b.Folders.RoamingAppData}\Code\User\settings.json", "{\n  // My new laptop\n  \"editor.fontSize\": 16\n}\n");
        customize?.Invoke(b);
        return b.Build(Temp.Combine("targets", name));
    }

    public RestoreRequest Request(IMachine target, IEnumerable<RootMapping>? mappings = null, IReadOnlyDictionary<string, ConflictDecision>? decisions = null, Func<ArtifactSummary, bool>? select = null)
        => new(Overview, Password, target, Overview.Artifacts.Where(select ?? (_ => true)).Select(a => a.Record.Key).ToHashSet(),
            [.. mappings ?? []], decisions ?? new Dictionary<string, ConflictDecision>(), Temp.Combine("work"));

    public RestorePlanner Planner() => new(Archive, NullLogger<RestorePlanner>.Instance);

    public ValueTask DisposeAsync()
    {
        BackupReader.Close(Overview);
        Temp.Dispose();
        return ValueTask.CompletedTask;
    }
}
