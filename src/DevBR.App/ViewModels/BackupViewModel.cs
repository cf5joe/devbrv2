using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevBR.App.Services;

namespace DevBR.App.ViewModels;

public sealed record WizardStep(int Number, string Title, string Description);

public sealed record SelectedItemSummary(string Owner, string Name, string? Badge);

public sealed partial class BackupViewModel : PageViewModel
{
    private readonly CatalogSession _session;
    private readonly Navigator _navigator;
    private readonly DiscoveryViewModel _discovery;

    public BackupViewModel(CatalogSession session, Navigator navigator, DiscoveryViewModel discovery)
    {
        _session = session;
        _navigator = navigator;
        _discovery = discovery;
        _session.SnapshotChanged += (_, _) => Refresh();
        _session.SelectionChanged += (_, _) => Refresh();
    }

    public override string Title => "Backup";

    public override string Glyph => "";

    public IReadOnlyList<WizardStep> Steps { get; } =
    [
        new(1, "Select artifacts", "Review the inventory and choose supported items. Only the system baseline is selected by default."),
        new(2, "Add files and folders", "Include your own scripts, notes or projects, with overlap and self-inclusion checks."),
        new(3, "Review", "Exclusions, sensitive items, dependencies and the estimated size."),
        new(4, "Output", "Backup file, scratch location, compression, and optional encryption."),
        new(5, "Confirm the plan", "See exactly what will be captured before anything is copied."),
        new(6, "Back up", "Run the capture, verify the archive, and read the final report."),
    ];

    public ObservableCollection<SelectedItemSummary> Selected { get; } = [];

    [ObservableProperty]
    public partial bool HasDiscovery { get; set; }

    [ObservableProperty]
    public partial string? Summary { get; set; }

    [ObservableProperty]
    public partial bool RequiresEncryption { get; set; }

    public override async Task OnNavigatedToAsync()
    {
        await _discovery.OnNavigatedToAsync();
        Refresh();
    }

    [RelayCommand]
    private void GoToDiscovery() => _navigator.NavigateTo<DiscoveryViewModel>();

    [RelayCommand]
    private void ReviewSelection()
    {
        _discovery.Tab = DiscoveryTab.Backup;
        _navigator.NavigateTo<DiscoveryViewModel>();
    }

    private void Refresh()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(Refresh);
            return;
        }

        HasDiscovery = _session.Snapshot is not null;
        Selected.Clear();
        foreach (var artifact in _session.SelectedArtifacts)
        {
            var badge = artifact.Sensitivity switch
            {
                Domain.Sensitivity.Credential => "Credential",
                Domain.Sensitivity.ContainsRecognizedSecrets => "Contains secrets",
                Domain.Sensitivity.MayContainSecrets => "May contain secrets",
                _ => null,
            };
            Selected.Add(new SelectedItemSummary(DiscoveryText.Owner(artifact.OwnerToolId), artifact.DisplayName, badge));
        }

        Summary = HasDiscovery ? $"{Formatting.Count(Selected.Count, "item", "items")} selected for backup." : null;
        RequiresEncryption = _session.SelectionRequiresEncryption;
    }
}
