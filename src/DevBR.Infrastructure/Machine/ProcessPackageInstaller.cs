using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DevBR.Application.Restore;
using Microsoft.Extensions.Logging;

namespace DevBR.Infrastructure.Machine;

/// <summary>
/// Runs WinGet or an editor's command line for an approved installation, without a window, capturing
/// its output. Arguments are passed as a list (never a command string) and identifiers are validated
/// again here, so nothing from a backup can change what runs.
/// </summary>
public sealed partial class ProcessPackageInstaller(ILogger<ProcessPackageInstaller> logger) : IPackageInstaller
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    public async Task<PackageInstallOutcome> InstallAsync(PackageInstallRequest request, CancellationToken cancellationToken)
    {
        if (!Identifier().IsMatch(request.PackageId) || (request.Version is not null && !VersionPattern().IsMatch(request.Version)))
        {
            return new PackageInstallOutcome(false, -1, "The package identifier is not valid.");
        }

        var (executable, arguments) = request.Kind switch
        {
            PackageKind.WinGet => (Find("winget.exe"), WinGetArguments(request)),
            _ => (FindEditor(request.Tool), EditorArguments(request)),
        };

        if (executable is null)
        {
            return new PackageInstallOutcome(false, -1, request.Kind == PackageKind.WinGet
                ? "WinGet was not found on this computer."
                : $"The '{request.Tool}' command line was not found. Start the editor once, or add it to PATH, then try again.");
        }

        // Editor launchers are .cmd files, which must run through cmd.exe; identifiers are already validated.
        var startInfo = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { ArgumentList = { "/d", "/c", executable } }
            : new ProcessStartInfo(executable);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;

        logger.LogInformation("Running {Executable} for {PackageId}.", Path.GetFileName(executable), request.PackageId);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The installer did not start.");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new PackageInstallOutcome(false, -1, "The installer did not finish within 30 minutes and was stopped.");
        }

        var text = output.ToString();
        logger.LogInformation("{PackageId} installer exited with {ExitCode}.", request.PackageId, process.ExitCode);
        return new PackageInstallOutcome(process.ExitCode == 0, process.ExitCode, text);
    }

    private static List<string> WinGetArguments(PackageInstallRequest request)
    {
        List<string> arguments = ["install", "--id", request.PackageId, "--exact", "--source", "winget", "--silent", "--disable-interactivity",
            "--accept-package-agreements", "--accept-source-agreements"];
        if (request.Version is not null)
        {
            arguments.AddRange(["--version", request.Version]);
        }

        return arguments;
    }

    private static List<string> EditorArguments(PackageInstallRequest request)
        => ["--install-extension", request.Version is null ? request.PackageId : $"{request.PackageId}@{request.Version}"];

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            if (output.Length < 64 * 1024)
            {
                output.AppendLine(line);
            }
        }
    }

    private static string? FindEditor(string tool)
    {
        if (tool is not ("code" or "code-insiders" or "cursor"))
        {
            return null;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] known = tool switch
        {
            "code" => [Path.Combine(local, "Programs", "Microsoft VS Code", "bin", "code.cmd"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "bin", "code.cmd")],
            "code-insiders" => [Path.Combine(local, "Programs", "Microsoft VS Code Insiders", "bin", "code-insiders.cmd")],
            _ => [Path.Combine(local, "Programs", "cursor", "resources", "app", "bin", "cursor.cmd")],
        };

        return known.FirstOrDefault(File.Exists) ?? Find(tool + ".cmd");
    }

    /// <summary>Looks on PATH and in the WindowsApps alias folder; never the current directory.</summary>
    private static string? Find(string fileName)
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps"));
        return candidates.Select(d => Path.Combine(d, fileName)).FirstOrDefault(File.Exists);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_.]{0,127}$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.\-+]{0,63}$")]
    private static partial Regex VersionPattern();
}
