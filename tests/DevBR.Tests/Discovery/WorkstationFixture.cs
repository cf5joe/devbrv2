using DevBR.Discovery;
using DevBR.Simulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBR.Tests.Discovery;

/// <summary>Builds the simulated developer workstation once and runs discovery against it.</summary>
public sealed class WorkstationFixture : IAsyncLifetime
{
    private readonly TempDirectory _temp = new();

    public SimulatedMachine Machine { get; private set; } = null!;

    /// <summary>Default options plus the E:\Archive custom root.</summary>
    public DiscoverySnapshot Snapshot { get; private set; } = null!;

    public static DiscoveryEngine Engine() => new(DiscoveryEngine.DefaultProviders(), NullLogger<DiscoveryEngine>.Instance);

    public async ValueTask InitializeAsync()
    {
        Machine = SampleMachines.DeveloperWorkstation(_temp.Combine("alice"));
        Snapshot = await Engine().RunAsync(Machine, DiscoveryOptions.Default with { ExtraRoots = [@"E:\Archive"] }, null, CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        _temp.Dispose();
        return ValueTask.CompletedTask;
    }
}
