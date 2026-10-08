using CommunityToolkit.Mvvm.Input;
using DevBR.App.Services;
using DevBR.Infrastructure;

namespace DevBR.App.ViewModels;

public sealed record JourneyStep(string Name, bool IsLast)
{
    // Lists without an item container announce ToString(); keep it a readable name, never a record dump.
    public override string ToString() => Name;
}

public sealed partial class OverviewViewModel(Navigator navigator, RestoreViewModel restore, AppPaths paths) : PageViewModel
{
    public override string Title => "Overview";

    public override string Glyph => "";

    public IReadOnlyList<JourneyStep> Journey { get; } =
        [.. new[] { "Discover", "Review", "Select", "Back up", "Transfer", "Inspect", "Preflight", "Restore", "Verify" }
            .Select((name, index) => new JourneyStep(name, IsLast: index == 8))];

    public string DataFolder => paths.Root;

    public string ElevationSummary => BuildInfo.IsElevated
        ? "DevBR is running as administrator."
        : "DevBR is running without administrator rights. It asks for approval only for specific privileged steps.";

    [RelayCommand]
    private void Discover() => navigator.NavigateTo<DiscoveryViewModel>();

    [RelayCommand]
    private async Task OpenBackupAsync()
    {
        navigator.NavigateTo<RestoreViewModel>();
        await restore.BrowseCommand.ExecuteAsync(null);
    }
}
