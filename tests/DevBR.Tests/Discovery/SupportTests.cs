using DevBR.Discovery;
using DevBR.Discovery.Support;
using DevBR.Domain;
using DevBR.Infrastructure;
using DevBR.Infrastructure.State;
using DevBR.Simulation;

namespace DevBR.Tests.Discovery;

public sealed class SupportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("https://alice:ghp_abcdefghijklmnopqrstuv@github.com/a/b.git", "https://alice:***@github.com/a/b.git")]
    [InlineData("https://ghp_abcdefghijklmnopqrstuvwxyz0123@github.com/a/b.git", "https://***@github.com/a/b.git")]
    [InlineData("https://github.com/a/b.git", "https://github.com/a/b.git")]
    [InlineData("git@github.com:a/b.git", "git@github.com:a/b.git")]
    [InlineData("https://host/x?token=abc123&page=2", "https://host/x?token=***&page=2")]
    public void Credentials_in_urls_are_redacted(string url, string expected)
        => Assert.Equal(expected, SecretDetector.RedactUrl(url));

    [Theory]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("DB_PASSWORD", true)]
    [InlineData("JAVA_HOME", false)]
    [InlineData("TOKEN_CACHE_DIR", false)]
    [InlineData("PATH", false)]
    [InlineData("AUTHOR", false)]
    public void Secret_variable_names_are_recognized(string name, bool secret)
        => Assert.Equal(secret, SecretDetector.IsSecretName(name));

    [Theory]
    [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwxyz", true)]
    [InlineData("AKIAABCDEFGHIJKLMNOP", true)]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----", true)]
    [InlineData("C:\\Program Files\\Git\\cmd", false)]
    public void Secret_values_are_recognized(string value, bool secret)
        => Assert.Equal(secret, SecretDetector.LooksLikeSecretValue(value));

    [Fact]
    public void Shell_links_round_trip()
        => Assert.Equal(@"C:\Program Files\Git\git-bash.exe", ShellLinkReader.ReadTarget(ShellLinkWriter.Create(@"C:\Program Files\Git\git-bash.exe")));

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x4C, 0, 0, 0, 1, 2, 3 })]
    public void Malformed_shell_links_are_ignored(byte[] data) => Assert.Null(ShellLinkReader.ReadTarget(data));

    [Fact]
    public void Git_config_sections_subsections_and_includes_are_parsed()
    {
        var config = GitConfig.Parse("[user]\n\tname = Alice ; comment\n[remote \"origin\"]\n\turl = https://x/y.git\n[includeIf \"gitdir:~/work/\"]\n\tpath = ~/.work\n[include]\n\tpath = ~/.extra\n");
        Assert.Equal("Alice", config.Get("user", null, "name"));
        Assert.Equal("https://x/y.git", config.Get("remote", "origin", "url"));
        Assert.Equal(["~/.work", "~/.extra"], config.IncludePaths());
    }

    [Theory]
    [InlineData(@"C:\Users\alice\AppData\Roaming\Code\User\settings.json", LogicalRootKind.RoamingAppData, @"Code\User\settings.json")]
    [InlineData(@"C:\Users\alice\AppData\Local\Programs\x", LogicalRootKind.LocalAppData, @"Programs\x")]
    [InlineData(@"C:\Users\alice\.gitconfig", LogicalRootKind.UserProfile, ".gitconfig")]
    [InlineData(@"C:\Users\alicea\.gitconfig", LogicalRootKind.CustomRoot, "")]
    public void Paths_map_to_the_most_specific_logical_root(string path, LogicalRootKind kind, string relative)
    {
        var folders = new SimulatedMachineBuilder("alice").Folders;
        var logical = Paths.ToLogical(folders, path);
        Assert.Equal(kind, logical.Root);
        Assert.Equal(relative, logical.RelativePath);
    }

    [Theory]
    [InlineData("7.6.6", "7.6.6.0")]
    [InlineData("v1.2", "1.2.0")]
    [InlineData("2.55.0.windows.1", "2.55.0.windows.1")]
    public void Versions_are_normalized_for_comparison(string a, string b)
        => Assert.Equal(DiscoveryEngine.NormalizeVersion(a), DiscoveryEngine.NormalizeVersion(b));

    [Fact]
    public async Task Catalog_keeps_snapshots_selection_and_preferences_per_machine()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("state"));
        paths.EnsureCreated();
        var database = new StateDatabase(paths, Loggers.For<StateDatabase>());
        await database.InitializeAsync(Ct);
        var catalog = new CatalogStore(database, Loggers.For<CatalogStore>());

        var machine = SampleMachines.CleanTarget(temp.Combine("machine"));
        var snapshot = await WorkstationFixture.Engine().RunAsync(machine, DiscoveryOptions.Default, null, Ct);
        await catalog.SaveAsync(snapshot, Ct);

        var loaded = await catalog.LoadLatestAsync(snapshot.MachineKey, Ct);
        Assert.NotNull(loaded);
        Assert.Equal(snapshot.Items.Count, loaded.Items.Count);
        Assert.Equal(snapshot.Artifacts.Select(a => a.Id), loaded.Artifacts.Select(a => a.Id));
        Assert.Null(await catalog.LoadLatestAsync("local:OTHER:someone", Ct));

        await catalog.SetSelectionAsync(snapshot.MachineKey, "vscode:default:settings", true, Ct);
        await catalog.SetSelectionAsync(snapshot.MachineKey, DiscoveryEngine.InventoryArtifactId, false, Ct);
        var selection = await catalog.GetSelectionAsync(snapshot.MachineKey, Ct);
        Assert.True(selection["vscode:default:settings"]);
        Assert.False(selection[DiscoveryEngine.InventoryArtifactId]);

        await catalog.SetPreferencesAsync(snapshot.MachineKey, new DiscoveryPreferences([@"E:\Archive"], true, false), Ct);
        Assert.Equal([@"E:\Archive"], (await catalog.GetPreferencesAsync(snapshot.MachineKey, Ct)).ExtraRoots);

        for (var i = 0; i < 7; i++)
        {
            await catalog.SaveAsync(snapshot with { RunId = Guid.NewGuid(), CompletedAt = DateTimeOffset.UtcNow.AddMinutes(i + 1) }, Ct);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
