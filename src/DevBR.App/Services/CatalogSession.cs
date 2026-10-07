using DevBR.Discovery;
using DevBR.Domain;
using DevBR.Infrastructure.State;

namespace DevBR.App.Services;

/// <summary>
/// The latest discovery snapshot of the current machine and the user's backup selection, shared by the
/// Discovery and Backup pages. Selections are persisted per machine and survive new discovery runs.
/// </summary>
public sealed class CatalogSession(CatalogStore catalog, MachineContext machine)
{
    private Dictionary<string, bool> _overrides = new(StringComparer.Ordinal);

    public event EventHandler? SnapshotChanged;

    public event EventHandler? SelectionChanged;

    public DiscoverySnapshot? Snapshot { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Snapshot = await catalog.LoadLatestAsync(machine.MachineKey, cancellationToken);
        _overrides = new Dictionary<string, bool>(await catalog.GetSelectionAsync(machine.MachineKey, cancellationToken), StringComparer.Ordinal);
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetSnapshotAsync(DiscoverySnapshot snapshot, CancellationToken cancellationToken)
    {
        await catalog.SaveAsync(snapshot, cancellationToken);
        Snapshot = snapshot;
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Inventory-only and blocked artifacts can never be selected for backup content.</summary>
    public static bool CanSelect(MigrationArtifact artifact)
        => artifact.Eligibility is BackupEligibility.Eligible or BackupEligibility.ExcludedByDefault;

    public bool IsSelected(MigrationArtifact artifact)
        => CanSelect(artifact) && (_overrides.TryGetValue(artifact.Id, out var selected) ? selected : artifact.SelectedByDefault);

    public async Task SetSelectedAsync(MigrationArtifact artifact, bool selected, CancellationToken cancellationToken)
    {
        if (!CanSelect(artifact))
        {
            return;
        }

        _overrides[artifact.Id] = selected;
        await catalog.SetSelectionAsync(machine.MachineKey, artifact.Id, selected, cancellationToken);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResetSelectionAsync(CancellationToken cancellationToken)
    {
        _overrides.Clear();
        await catalog.ResetSelectionAsync(machine.MachineKey, cancellationToken);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MigrationArtifact> SelectedArtifacts
        => Snapshot is null ? [] : [.. Snapshot.Artifacts.Where(IsSelected)];

    /// <summary>True when a selected artifact holds recognized secrets, which makes encryption mandatory.</summary>
    public bool SelectionRequiresEncryption
        => SelectedArtifacts.Any(a => a.Sensitivity is Sensitivity.ContainsRecognizedSecrets or Sensitivity.Credential);
}
