using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevBR.Discovery;
using Microsoft.Extensions.Logging;

namespace DevBR.Infrastructure.State;

/// <param name="ExtraRoots">User-added discovery roots for this machine.</param>
public sealed record DiscoveryPreferences(IReadOnlyList<string> ExtraRoots, bool ScanFixedDrives, bool IncludeOtherUserProfiles)
{
    public static DiscoveryPreferences Default { get; } = new([], true, false);
}

/// <summary>Local discovery catalog: recent snapshots, backup selections and discovery preferences.</summary>
public sealed class CatalogStore(StateDatabase database, ILogger<CatalogStore> logger)
{
    private const int RunsKeptPerMachine = 5;

    private static readonly JsonSerializerOptions Json = CreateOptions();

    public async Task SaveAsync(DiscoverySnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO discovery_runs (id, machine_key, started_utc, completed_utc, cancelled, item_count, artifact_count, snapshot_json)
                VALUES ($id, $machine, $started, $completed, $cancelled, $items, $artifacts, $json);
                """;
            insert.Parameters.AddWithValue("$id", snapshot.RunId.ToString());
            insert.Parameters.AddWithValue("$machine", snapshot.MachineKey);
            insert.Parameters.AddWithValue("$started", snapshot.StartedAt.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$completed", snapshot.CompletedAt.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$cancelled", snapshot.Cancelled ? 1 : 0);
            insert.Parameters.AddWithValue("$items", snapshot.Items.Count);
            insert.Parameters.AddWithValue("$artifacts", snapshot.Artifacts.Count);
            insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, Json));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = """
                DELETE FROM discovery_runs WHERE machine_key = $machine AND id NOT IN (
                    SELECT id FROM discovery_runs WHERE machine_key = $machine ORDER BY completed_utc DESC LIMIT $keep);
                """;
            prune.Parameters.AddWithValue("$machine", snapshot.MachineKey);
            prune.Parameters.AddWithValue("$keep", RunsKeptPerMachine);
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiscoverySnapshot?> LoadLatestAsync(string machineKey, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM discovery_runs WHERE machine_key = $machine ORDER BY completed_utc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$machine", machineKey);

        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DiscoverySnapshot>(json, Json);
        }
        catch (JsonException ex)
        {
            // A catalog written by an incompatible build is simply ignored; the user can run discovery again.
            logger.LogWarning(ex, "Ignoring an unreadable discovery snapshot for {Machine}.", machineKey);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<string, bool>> GetSelectionAsync(string machineKey, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT artifact_id, selected FROM backup_selection WHERE machine_key = $machine;";
        command.Parameters.AddWithValue("$machine", machineKey);

        var selection = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            selection[reader.GetString(0)] = reader.GetInt64(1) != 0;
        }

        return selection;
    }

    public async Task SetSelectionAsync(string machineKey, string artifactId, bool selected, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO backup_selection (machine_key, artifact_id, selected) VALUES ($machine, $artifact, $selected)
            ON CONFLICT (machine_key, artifact_id) DO UPDATE SET selected = excluded.selected;
            """;
        command.Parameters.AddWithValue("$machine", machineKey);
        command.Parameters.AddWithValue("$artifact", artifactId);
        command.Parameters.AddWithValue("$selected", selected ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetSelectionAsync(string machineKey, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM backup_selection WHERE machine_key = $machine;";
        command.Parameters.AddWithValue("$machine", machineKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DiscoveryPreferences> GetPreferencesAsync(string machineKey, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT preferences FROM discovery_preferences WHERE machine_key = $machine;";
        command.Parameters.AddWithValue("$machine", machineKey);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? DiscoveryPreferences.Default : JsonSerializer.Deserialize<DiscoveryPreferences>(json, Json) ?? DiscoveryPreferences.Default;
    }

    public async Task SetPreferencesAsync(string machineKey, DiscoveryPreferences preferences, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO discovery_preferences (machine_key, preferences) VALUES ($machine, $json)
            ON CONFLICT (machine_key) DO UPDATE SET preferences = excluded.preferences;
            """;
        command.Parameters.AddWithValue("$machine", machineKey);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(preferences, Json));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
