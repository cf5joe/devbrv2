using System.IO;
using DevBR.Application.Machine;
using DevBR.Discovery;
using DevBR.Infrastructure;
using DevBR.Infrastructure.Machine;
using DevBR.Simulation;

namespace DevBR.App.Services;

/// <summary>
/// The computer DevBR is currently looking at: this PC, or (in development builds) a simulated machine
/// folder. Switching raises <see cref="Changed"/> so pages reload that machine's catalog.
/// </summary>
public sealed class MachineContext
{
    private readonly AppPaths _paths;

    public MachineContext(AppPaths paths)
    {
        _paths = paths;
        Current = new WindowsMachine();
    }

    public event EventHandler? Changed;

    public IMachine Current { get; private set; }

    public string MachineKey => DiscoveryEngine.MachineKey(Current);

    public string SimulationsRoot => Path.Combine(_paths.Root, "simulations");

    public string Label => Current.IsSimulated
        ? $"Simulated: {Current.Info.ComputerName} ({Current.Info.UserName})"
        : $"{Current.Info.ComputerName} ({Current.Info.UserName})";

    public void UseThisComputer()
    {
        Current = new WindowsMachine();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UseSimulated(string folder)
    {
        if (!BuildInfo.IsDevelopmentBuild)
        {
            throw new InvalidOperationException("Simulated machines are available in development builds only.");
        }

        Current = SimulatedMachine.Load(folder);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string CreateSampleWorkstation()
    {
        var folder = Path.Combine(SimulationsRoot, "alice-workstation");
        SampleMachines.DeveloperWorkstation(folder);
        return folder;
    }

    public string CreateCleanTarget()
    {
        var folder = Path.Combine(SimulationsRoot, "clean-target");
        SampleMachines.CleanTarget(folder);
        return folder;
    }
}
