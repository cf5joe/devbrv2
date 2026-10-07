using DevBR.Application.Settings;
using DevBR.Domain;
using DevBR.Infrastructure;
using DevBR.Infrastructure.Settings;
using DevBR.Infrastructure.State;

namespace DevBR.Tests.Infrastructure;

public sealed class StateTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _paths;

    public StateTests()
    {
        _paths = new AppPaths(_temp.Path);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Settings_round_trip_and_survive_restart()
    {
        var store = new JsonSettingsStore(_paths, Loggers.For<JsonSettingsStore>());
        Assert.Equal(AppSettings.Default, store.Current);

        await store.SaveAsync(new AppSettings(ThemePreset.Plum, "#22C7E0", @"D:\Scratch"), Ct);

        var reloaded = new JsonSettingsStore(_paths, Loggers.For<JsonSettingsStore>());
        Assert.Equal(ThemePreset.Plum, reloaded.Current.Theme);
        Assert.Equal("#22C7E0", reloaded.Current.AccentHex);
        Assert.Equal(@"D:\Scratch", reloaded.Current.ScratchDirectory);
    }

    [Fact]
    public void Unreadable_settings_fall_back_to_defaults_and_keep_a_copy()
    {
        File.WriteAllText(_paths.SettingsPath, "{ this is not json");

        var store = new JsonSettingsStore(_paths, Loggers.For<JsonSettingsStore>());

        Assert.Equal(AppSettings.Default, store.Current);
        Assert.True(File.Exists(_paths.SettingsPath + ".unreadable"));
    }

    [Fact]
    public async Task Database_migrations_are_idempotent_and_activity_is_recorded()
    {
        var database = new StateDatabase(_paths, Loggers.For<StateDatabase>());
        await database.InitializeAsync(Ct);
        await database.InitializeAsync(Ct);

        var activity = new ActivityStore(database, Loggers.For<ActivityStore>());
        await activity.AddAsync(EventSeverity.Warning, "Test", "Something happened", "detail", Ct);
        var records = await activity.ListRecentAsync(10, Ct);

        var record = Assert.Single(records);
        Assert.Equal(EventSeverity.Warning, record.Severity);
        Assert.Equal("detail", record.Detail);
    }
}
