using System.Text.Json;
using DevBR.Application.Machine;

namespace DevBR.Simulation;

/// <summary>Applies restore changes to a simulated machine's fixture folder and registry.</summary>
public sealed class SimulatedMachineWriter(SimulatedMachine machine) : IMachineWriter
{
    private const string UserEnvironment = "Environment";
    private const string MachineEnvironment = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>Called before every change; tests use it to simulate a crash at a precise moment.</summary>
    public Action<string>? BeforeWrite { get; set; }

    public int Broadcasts { get; private set; }

    public void WriteFileAtomic(string path, byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        WriteFileAtomic(path, stream);
    }

    public void WriteFileAtomic(string path, Stream content)
    {
        BeforeWrite?.Invoke(path);
        var backing = machine.MapPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(backing)!);
        var temp = backing + $".devbr-{Guid.NewGuid():N}.tmp";
        using (var output = File.Create(temp))
        {
            content.CopyTo(output);
        }

        File.Move(temp, backing, overwrite: true);
    }

    public void CreateDirectory(string path)
    {
        BeforeWrite?.Invoke(path);
        Directory.CreateDirectory(machine.MapPath(path));
    }

    public void DeleteFile(string path)
    {
        BeforeWrite?.Invoke(path);
        var backing = machine.MapPath(path);
        if (File.Exists(backing))
        {
            File.Delete(backing);
        }
    }

    public void DeleteDirectoryTree(string path)
    {
        BeforeWrite?.Invoke(path);
        var backing = machine.MapPath(path);
        if (Directory.Exists(backing))
        {
            Directory.Delete(backing, recursive: true);
        }
    }

    public void SetUserEnvironmentVariable(string name, string? value, RegistryValueKind kind)
    {
        BeforeWrite?.Invoke($"ENV:User:{name}");
        Set("HKCU", UserEnvironment, name, value, kind);
    }

    /// <summary>Machine scope: only reachable through a simulated elevated session.</summary>
    internal void SetMachineEnvironmentVariable(string name, string? value, RegistryValueKind kind)
    {
        BeforeWrite?.Invoke($"ENV:Machine:{name}");
        Set("HKLM64", MachineEnvironment, name, value, kind);
    }

    public void BroadcastEnvironmentChange() => Broadcasts++;

    private void Set(string hive, string key, string name, string? value, RegistryValueKind kind)
        => ((SimulatedRegistry)machine.Registry).SetValue(machine.Root, hive, key, name,
            value is null ? null : new RegistryValueDefinition(kind, JsonSerializer.SerializeToElement(value)));
}

/// <summary>
/// Stands in for UAC and the broker on a simulated machine: approves or declines, and re-validates every
/// change against the approved effects exactly as the real broker does.
/// </summary>
public sealed class SimulatedElevationProvider(SimulatedMachine machine, SimulatedMachineWriter writer, Func<bool> approve, Func<Guid, IReadOnlySet<string>?> approvedEffects) : IElevationProvider
{
    public SimulatedElevationProvider(SimulatedMachine machine, SimulatedMachineWriter writer, bool approve, Func<Guid, IReadOnlySet<string>?> approvedEffects)
        : this(machine, writer, () => approve, approvedEffects)
    {
    }

    public int Requests { get; private set; }

    /// <summary>Asks <c>approve</c> each time, as Windows shows a UAC prompt each time.</summary>
    public Task<IElevatedSession?> RequestAsync(CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult<IElevatedSession?>(approve() ? new Session(machine, writer, approvedEffects) : null);
    }

    private sealed class Session(SimulatedMachine machine, SimulatedMachineWriter writer, Func<Guid, IReadOnlySet<string>?> approvedEffects) : IElevatedSession
    {
        public Task ApplyMachineEnvironmentAsync(Guid jobId, string approvalHash, IReadOnlyList<EnvironmentChange> changes, CancellationToken cancellationToken)
        {
            var approved = approvedEffects(jobId) ?? throw new UnauthorizedAccessException("This change is not part of an approved restore plan.");
            foreach (var change in changes)
            {
                if (!RestoreEffects.Permits(approved, change))
                {
                    throw new UnauthorizedAccessException($"The change to {change.Name} is not part of the approved plan.");
                }

                var current = machine.ReadEnvironment(RegistryHive.LocalMachine).GetValueOrDefault(change.Name)?.AsString();
                if (!string.Equals(current, change.ExpectedCurrentValue, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"{change.Name} changed since it was checked; nothing was written.");
                }

                writer.SetMachineEnvironmentVariable(change.Name, change.NewValue, change.Expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
