using System.Text.Json;
using System.Text.Json.Nodes;
using DevBR.Application.Machine;
using DevBR.Application.Restore;

namespace DevBR.Simulation;

/// <summary>
/// Pretends to run WinGet and editor CLIs on a simulated machine: it leaves the traces a real installation
/// would (an Add/Remove Programs entry and executable, or an extensions.json entry) so detection finds them.
/// </summary>
public sealed class SimulatedPackageInstaller(SimulatedMachine machine, SimulatedMachineWriter writer) : IPackageInstaller
{
    private static readonly Dictionary<string, (string DisplayName, string Version, string Executable, bool Machine)> Packages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenJS.NodeJS.LTS"] = ("Node.js", "22.11.0", @"{ProgramFiles}\nodejs\node.exe", true),
        ["Python.Python.3.12"] = ("Python 3.12.7 (64-bit)", "3.12.7", @"{LocalAppData}\Programs\Python\Python312\python.exe", false),
        ["Git.Git"] = ("Git", "2.47.0", @"{ProgramFiles}\Git\cmd\git.exe", true),
        ["Docker.DockerDesktop"] = ("Docker Desktop", "4.36.0", @"{ProgramFiles}\Docker\Docker\Docker Desktop.exe", true),
        ["astral-sh.uv"] = ("uv", "0.5.4", @"{LocalAppData}\Microsoft\WinGet\Links\uv.exe", false),
    };

    public List<PackageInstallRequest> Requests { get; } = [];

    /// <summary>Package ids that fail with exit code 1, for testing failure reporting.</summary>
    public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<PackageInstallOutcome> InstallAsync(PackageInstallRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Failing.Contains(request.PackageId))
        {
            return Task.FromResult(new PackageInstallOutcome(false, 1, $"Simulated failure installing {request.PackageId}."));
        }

        return Task.FromResult(request.Kind == PackageKind.WinGet ? InstallPackage(request) : InstallExtension(request));
    }

    private PackageInstallOutcome InstallPackage(PackageInstallRequest request)
    {
        if (!Packages.TryGetValue(request.PackageId, out var package))
        {
            return new PackageInstallOutcome(false, -1978335212, $"No package found matching input criteria ({request.PackageId}).");
        }

        var folders = machine.Folders;
        var exe = package.Executable.Replace("{ProgramFiles}", folders.ProgramFiles, StringComparison.Ordinal).Replace("{LocalAppData}", folders.LocalAppData, StringComparison.Ordinal);
        writer.WriteFileAtomic(exe, "MZ simulated executable"u8.ToArray());

        var registry = (SimulatedRegistry)machine.Registry;
        var hive = package.Machine ? "HKLM64" : "HKCU";
        var key = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{request.PackageId}";
        void Set(string name, string value) => registry.SetValue(machine.Root, hive, key, name, new RegistryValueDefinition(RegistryValueKind.String, JsonSerializer.SerializeToElement(value)));
        Set("DisplayName", package.DisplayName);
        Set("DisplayVersion", package.Version);
        Set("InstallLocation", Path.GetDirectoryName(exe)! + "\\");
        Set("UninstallString", $"\"{Path.GetDirectoryName(exe)}\\uninstall.exe\"");

        // Tools without an uninstall entry are found on PATH, as the WinGet Links folder is on a real computer.
        if (package.DisplayName == "uv")
        {
            var folder = Path.GetDirectoryName(exe)!;
            var path = machine.ReadEnvironment(RegistryHive.CurrentUser).GetValueOrDefault("Path")?.AsString();
            if (path is null || !path.Split(';').Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                writer.SetUserEnvironmentVariable("Path", RestoreEffects.AppendPathEntry(path, folder), RegistryValueKind.ExpandString);
            }
        }

        return new PackageInstallOutcome(true, 0, $"Successfully installed {package.DisplayName} {package.Version} (simulated).");
    }

    private PackageInstallOutcome InstallExtension(PackageInstallRequest request)
    {
        var folder = request.Tool switch
        {
            "code" => ".vscode",
            "code-insiders" => ".vscode-insiders",
            "cursor" => ".cursor",
            _ => null,
        };

        if (folder is null)
        {
            return new PackageInstallOutcome(false, 1, $"'{request.Tool}' is not recognized as an editor command line.");
        }

        var manifest = Path.Combine(machine.Folders.UserProfile, folder, "extensions", "extensions.json");
        var list = machine.FileSystem.FileExists(manifest) && JsonNode.Parse(machine.FileSystem.ReadText(manifest, 1 << 22) ?? "[]") is JsonArray existing ? existing : [];
        if (!list.OfType<JsonObject>().Any(e => string.Equals((string?)e["identifier"]?["id"], request.PackageId, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new JsonObject
            {
                ["identifier"] = new JsonObject { ["id"] = request.PackageId },
                ["version"] = request.Version ?? "1.0.0",
            });
        }

        writer.WriteFileAtomic(manifest, System.Text.Encoding.UTF8.GetBytes(list.ToJsonString()));
        return new PackageInstallOutcome(true, 0, $"Extension '{request.PackageId}' was successfully installed (simulated).");
    }
}
