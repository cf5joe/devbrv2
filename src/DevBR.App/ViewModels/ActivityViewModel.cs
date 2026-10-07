using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using DevBR.App.Services;
using DevBR.Domain;
using DevBR.Infrastructure;
using DevBR.Infrastructure.State;

namespace DevBR.App.ViewModels;

public sealed record ActivityItem(string Glyph, string SeverityLabel, string Time, string Category, string Message, string? Detail, EventSeverity Severity);

public sealed partial class ActivityViewModel : PageViewModel
{
    private readonly ActivityStore _store;
    private readonly AppPaths _paths;
    private readonly IDialogService _dialogs;

    public ActivityViewModel(ActivityStore store, AppPaths paths, IDialogService dialogs)
    {
        _store = store;
        _paths = paths;
        _dialogs = dialogs;
        _store.Added += (_, record) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => Items.Insert(0, ToItem(record)));
    }

    public override string Title => "Activity";

    public override string Glyph => "";

    public ObservableCollection<ActivityItem> Items { get; } = [];

    public override Task OnNavigatedToAsync() => RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var records = await _store.ListRecentAsync(200, CancellationToken.None);
        Items.Clear();
        foreach (var record in records)
        {
            Items.Add(ToItem(record));
        }
    }

    [RelayCommand]
    private void OpenLogsFolder() => _dialogs.RevealFolder(_paths.LogsDirectory);

    private static ActivityItem ToItem(ActivityRecord record) => new(
        record.Severity switch
        {
            EventSeverity.Error => "",
            EventSeverity.Warning => "",
            _ => "",
        },
        record.Severity switch
        {
            EventSeverity.Error => "Error",
            EventSeverity.Warning => "Warning",
            _ => "Info",
        },
        record.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        record.Category,
        record.Message,
        record.Detail,
        record.Severity);
}
