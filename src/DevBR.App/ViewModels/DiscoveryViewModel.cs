using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevBR.App.Services;
using DevBR.Discovery;
using DevBR.Domain;
using DevBR.Infrastructure;
using DevBR.Infrastructure.State;
using Microsoft.Extensions.Logging;

namespace DevBR.App.ViewModels;

public enum DiscoveryTab
{
    Inventory,
    Backup,
    Coverage,
}

/// <summary>
/// Discovery page: scope, user-initiated scanning with live counts, inventory browsing with search,
/// filters and details, backup selection, and the coverage report. Nothing runs until the user starts it.
/// </summary>
public sealed partial class DiscoveryViewModel : PageViewModel
{
    private const string AllFilter = "All";

    private readonly DiscoveryEngine _engine;
    private readonly MachineContext _machine;
    private readonly CatalogSession _session;
    private readonly CatalogStore _catalog;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly Infrastructure.State.ActivityStore _activity;
    private readonly ILogger<DiscoveryViewModel> _logger;
    private CancellationTokenSource? _scan;
    private bool _loaded;

    public DiscoveryViewModel(DiscoveryEngine engine, MachineContext machine, CatalogSession session, CatalogStore catalog,
        IDialogService dialogs, AppPaths paths, Infrastructure.State.ActivityStore activity, ILogger<DiscoveryViewModel> logger)
    {
        _engine = engine;
        _machine = machine;
        _session = session;
        _catalog = catalog;
        _dialogs = dialogs;
        _paths = paths;
        _activity = activity;
        _logger = logger;

        InventoryView = CollectionViewSource.GetDefaultView(Inventory);
        InventoryView.Filter = FilterInventory;
        ApplySort();

        _session.SnapshotChanged += (_, _) => Dispatch(Rebuild);
        _session.SelectionChanged += (_, _) => Dispatch(SyncSelection);
        _machine.Changed += async (_, _) =>
        {
            _loaded = false;
            await OnNavigatedToAsync();
        };
    }

    public override string Title => "Discovery";

    public override string Glyph => "";

    // --- Scope ----------------------------------------------------------------------------------

    public string MachineLabel => _machine.Label;

    public bool IsSimulated => _machine.Current.IsSimulated;

    public bool IsElevated => _machine.Current.Info.IsElevated;

    public string ElevationTitle => IsElevated ? "Running as administrator" : "Running as a standard user";

    public string ElevationDetail => IsElevated
        ? "Protected inventory sources can be read directly."
        : "Locations that need administrator rights are listed as inaccessible in the coverage report rather than skipped silently.";

    public ObservableCollection<string> Drives { get; } = [];

    public ObservableCollection<string> ExtraRoots { get; } = [];

    [ObservableProperty]
    public partial bool ScanFixedDrives { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeOtherUserProfiles { get; set; }

    // --- Scan state -----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand), nameof(AddRootCommand), nameof(RemoveRootCommand))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string? ScanStatus { get; set; }

    [ObservableProperty]
    public partial string? CurrentScope { get; set; }

    [ObservableProperty]
    public partial bool IsCancelling { get; set; }

    public ObservableCollection<CountChip> LiveCategories { get; } = [];

    public ObservableCollection<CountChip> LiveDrives { get; } = [];

    // --- Results --------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults), nameof(ResultSummary), nameof(IsPartial))]
    public partial DiscoverySnapshot? Snapshot { get; set; }

    public bool HasResults => Snapshot is not null;

    public bool IsPartial => Snapshot?.Cancelled == true;

    public string? ResultSummary => Snapshot is null ? null
        : $"{Formatting.Count(Snapshot.Items.Count, "record", "records")} found {Snapshot.CompletedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} in {(Snapshot.CompletedAt - Snapshot.StartedAt).TotalSeconds:0.0} s";

    [ObservableProperty]
    public partial DiscoveryTab Tab { get; set; } = DiscoveryTab.Inventory;

    public ObservableCollection<InventoryRow> Inventory { get; } = [];

    public ICollectionView InventoryView { get; }

    public ObservableCollection<string> CategoryFilters { get; } = [AllFilter];

    public IReadOnlyList<string> ScopeFilters { get; } = [AllFilter, "User", "All users", "Portable", "Store"];

    public IReadOnlyList<string> SortOptions { get; } = ["Name", "Category", "Confidence", "Location"];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CategoryFilter { get; set; } = AllFilter;

    [ObservableProperty]
    public partial string ScopeFilter { get; set; } = AllFilter;

    [ObservableProperty]
    public partial string SortBy { get; set; } = "Name";

    [ObservableProperty]
    public partial InventoryRow? SelectedItem { get; set; }

    [ObservableProperty]
    public partial int VisibleCount { get; set; }

    public ObservableCollection<ArtifactGroup> ArtifactGroups { get; } = [];

    [ObservableProperty]
    public partial string? SelectionSummary { get; set; }

    [ObservableProperty]
    public partial bool SelectionRequiresEncryption { get; set; }

    public ObservableCollection<CoverageRow> Coverage { get; } = [];

    public override async Task OnNavigatedToAsync()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        OnPropertyChanged(nameof(MachineLabel));
        OnPropertyChanged(nameof(IsSimulated));
        OnPropertyChanged(nameof(IsElevated));
        OnPropertyChanged(nameof(ElevationTitle));
        OnPropertyChanged(nameof(ElevationDetail));

        // Listing drive letters happens only once the user opens this page.
        Drives.Clear();
        foreach (var drive in _machine.Current.FileSystem.GetFixedDrives())
        {
            Drives.Add($"{drive.Root.TrimEnd('\\')}  {drive.Label}  {(drive.TotalBytes > 0 ? Formatting.Bytes(drive.TotalBytes) : string.Empty)}".Trim());
        }

        var preferences = await _catalog.GetPreferencesAsync(_machine.MachineKey, CancellationToken.None);
        ExtraRoots.Clear();
        foreach (var root in preferences.ExtraRoots)
        {
            ExtraRoots.Add(root);
        }

        ScanFixedDrives = preferences.ScanFixedDrives;
        IncludeOtherUserProfiles = preferences.IncludeOtherUserProfiles;
        await _session.LoadAsync(CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        await SavePreferencesAsync();
        _scan = new CancellationTokenSource();
        IsScanning = true;
        IsCancelling = false;
        ScanStatus = "Starting…";
        LiveCategories.Clear();
        LiveDrives.Clear();

        var excluded = new List<string> { _paths.Root };
        var options = new DiscoveryOptions(ScanFixedDrives, [.. ExtraRoots], excluded, IncludeOtherUserProfiles);
        var progress = new ThrottledProgress<DiscoveryProgress>(OnProgress);

        try
        {
            DiscoverySnapshot snapshot;
            using (progress)
            {
                snapshot = await _engine.RunAsync(_machine.Current, options, progress, _scan.Token);
            }

            await _session.SetSnapshotAsync(snapshot, CancellationToken.None);
            Tab = DiscoveryTab.Inventory;
            await _activity.AddAsync(snapshot.Cancelled ? EventSeverity.Warning : EventSeverity.Information, "Discovery",
                snapshot.Cancelled
                    ? $"Discovery of {_machine.Label} was cancelled; {snapshot.Items.Count} records kept."
                    : $"Discovered {snapshot.Items.Count} records and {snapshot.Artifacts.Count} backup items on {_machine.Label}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Discovery failed.");
            ScanStatus = $"Discovery failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            IsCancelling = false;
            _scan.Dispose();
            _scan = null;
        }
    }

    private bool CanStart() => !IsScanning;

    [RelayCommand(CanExecute = nameof(IsScanning))]
    private void Cancel()
    {
        IsCancelling = true;
        ScanStatus = "Cancelling — finishing the current folder…";
        _scan?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task AddRootAsync()
    {
        if (_dialogs.PickFolder("Add a folder to discover", null) is { } folder && !ExtraRoots.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            ExtraRoots.Add(folder);
            await SavePreferencesAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RemoveRootAsync(string? root)
    {
        if (root is not null && ExtraRoots.Remove(root))
        {
            await SavePreferencesAsync();
        }
    }

    [RelayCommand]
    private void ShowTab(string tab) => Tab = Enum.Parse<DiscoveryTab>(tab);

    [RelayCommand]
    private Task ResetSelectionAsync() => _session.ResetSelectionAsync(CancellationToken.None);

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        CategoryFilter = AllFilter;
        ScopeFilter = AllFilter;
    }

    partial void OnSearchTextChanged(string value) => RefreshView();

    partial void OnCategoryFilterChanged(string value) => RefreshView();

    partial void OnScopeFilterChanged(string value) => RefreshView();

    partial void OnSortByChanged(string value) => ApplySort();

    partial void OnScanFixedDrivesChanged(bool value) => _ = SavePreferencesIfLoadedAsync();

    partial void OnIncludeOtherUserProfilesChanged(bool value) => _ = SavePreferencesIfLoadedAsync();

    private void OnProgress(DiscoveryProgress progress)
    {
        if (IsCancelling)
        {
            return;
        }

        ScanStatus = $"{progress.ProvidersCompleted} of {progress.ProvidersTotal} sources done · {Formatting.Count(progress.FilesScanned, "entry", "entries")} scanned · {Formatting.Elapsed(progress.Elapsed)}";
        CurrentScope = progress.CurrentScope;
        Replace(LiveCategories, progress.ByCategory.OrderByDescending(c => c.Value).Select(c => new CountChip(DiscoveryText.Category(c.Key), c.Value)));
        Replace(LiveDrives, progress.ByDrive.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase).Select(d => new CountChip(d.Key, d.Value)));
    }

    private void Rebuild()
    {
        Snapshot = _session.Snapshot;
        Inventory.Clear();
        ArtifactGroups.Clear();
        Coverage.Clear();
        SelectedItem = null;

        if (Snapshot is null)
        {
            VisibleCount = 0;
            SelectionSummary = null;
            return;
        }

        var artifactsByItem = Snapshot.Artifacts
            .Where(a => a.InventoryItemId is not null)
            .ToLookup(a => a.InventoryItemId!, StringComparer.Ordinal);
        var artifactsByTool = Snapshot.Artifacts.ToLookup(a => a.OwnerToolId, StringComparer.Ordinal);

        foreach (var item in Snapshot.Items)
        {
            var related = artifactsByItem[item.Id].ToList();
            if (related.Count == 0 && item.Category is InventoryCategory.Configuration && item.ToolId is not null)
            {
                related = [.. artifactsByTool[item.ToolId]];
            }

            Inventory.Add(new InventoryRow(item, related));
        }

        Replace(CategoryFilters, new[] { AllFilter }.Concat(Inventory.Select(r => r.Category).Distinct().Order(StringComparer.CurrentCulture)));
        if (!CategoryFilters.Contains(CategoryFilter))
        {
            CategoryFilter = AllFilter;
        }

        // Defaults first, then tools in a stable order; inventory-only and excluded items stay visible.
        foreach (var group in Snapshot.Artifacts
            .Select(a => new ArtifactRow(a, _session))
            .GroupBy(r => r.Owner)
            .OrderBy(g => g.Any(r => r.IsDefault) ? 0 : 1)
            .ThenBy(g => g.Key, StringComparer.CurrentCulture))
        {
            ArtifactGroups.Add(new ArtifactGroup(group.Key, [.. group.OrderBy(r => r.Artifact.Kind == ArtifactKind.Credentials).ThenBy(r => r.Name, StringComparer.CurrentCulture)]));
        }

        foreach (var coverage in Snapshot.Coverage.OrderBy(c => c.Errors.Count == 0).ThenBy(c => c.Volume is null).ThenBy(c => c.Source, StringComparer.CurrentCulture))
        {
            Coverage.Add(new CoverageRow(coverage));
        }

        RefreshView();
        SyncSelection();
    }

    private void SyncSelection()
    {
        foreach (var row in ArtifactGroups.SelectMany(g => g.Rows))
        {
            row.Sync();
        }

        var selected = _session.SelectedArtifacts;
        var bytes = selected.Sum(a => a.EstimatedBytes ?? 0);
        SelectionSummary = selected.Count == 0
            ? "Nothing selected."
            : $"{Formatting.Count(selected.Count, "item", "items")} selected{(bytes > 0 ? $" · about {Formatting.Bytes(bytes)} of files (repositories are measured at backup time)" : string.Empty)}";
        SelectionRequiresEncryption = _session.SelectionRequiresEncryption;
    }

    private bool FilterInventory(object value)
    {
        var row = (InventoryRow)value;
        return row.Matches(SearchText)
            && (CategoryFilter == AllFilter || row.Category == CategoryFilter)
            && (ScopeFilter == AllFilter || row.Scope == ScopeFilter);
    }

    private void RefreshView()
    {
        InventoryView.Refresh();
        VisibleCount = InventoryView.Cast<object>().Count();
    }

    private void ApplySort()
    {
        InventoryView.SortDescriptions.Clear();
        switch (SortBy)
        {
            case "Category":
                InventoryView.SortDescriptions.Add(new SortDescription(nameof(InventoryRow.Category), ListSortDirection.Ascending));
                break;
            case "Confidence":
                InventoryView.SortDescriptions.Add(new SortDescription(nameof(InventoryRow.Confidence), ListSortDirection.Descending));
                break;
            case "Location":
                InventoryView.SortDescriptions.Add(new SortDescription(nameof(InventoryRow.PrimaryLocation), ListSortDirection.Ascending));
                break;
        }

        InventoryView.SortDescriptions.Add(new SortDescription(nameof(InventoryRow.Name), ListSortDirection.Ascending));
    }

    private async Task SavePreferencesIfLoadedAsync()
    {
        if (_loaded && !IsScanning)
        {
            await SavePreferencesAsync();
        }
    }

    private Task SavePreferencesAsync()
        => _catalog.SetPreferencesAsync(_machine.MachineKey, new DiscoveryPreferences([.. ExtraRoots], ScanFixedDrives, IncludeOtherUserProfiles), CancellationToken.None);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }
}
