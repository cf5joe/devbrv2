using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DevBR.Infrastructure.State;

/// <summary>
/// Local SQLite store for discovery catalogs, job journals and activity. Schema changes are applied as
/// ordered, append-only migrations.
/// </summary>
public sealed class StateDatabase(AppPaths paths, ILogger<StateDatabase> logger)
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE activity (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT    NOT NULL,
            severity      TEXT    NOT NULL,
            category      TEXT    NOT NULL,
            message       TEXT    NOT NULL,
            detail        TEXT    NULL
        );
        CREATE INDEX ix_activity_timestamp ON activity (timestamp_utc);

        CREATE TABLE jobs (
            id          TEXT PRIMARY KEY,
            kind        TEXT NOT NULL,
            state       TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            updated_utc TEXT NOT NULL,
            summary     TEXT NULL
        );

        -- Journal: intent is recorded before a side effect, outcome after it.
        CREATE TABLE job_operations (
            job_id       TEXT    NOT NULL REFERENCES jobs (id),
            operation_id TEXT    NOT NULL,
            sequence     INTEGER NOT NULL,
            intent       TEXT    NOT NULL,
            outcome      TEXT    NULL,
            intent_utc   TEXT    NOT NULL,
            outcome_utc  TEXT    NULL,
            PRIMARY KEY (job_id, operation_id)
        );
        """,
    ];

    public string ConnectionString { get; } = new SqliteConnectionStringBuilder
    {
        DataSource = paths.StateDatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        ForeignKeys = true,
    }.ToString();

    public int SchemaVersion => Migrations.Length;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(paths.StateDatabasePath)!);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);", cancellationToken).ConfigureAwait(false);

        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        var current = Convert.ToInt32(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        if (current > Migrations.Length)
        {
            throw new InvalidOperationException($"The state database was created by a newer DevBR (schema {current}); this version supports {Migrations.Length}.");
        }

        for (var version = current + 1; version <= Migrations.Length; version++)
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var migrate = connection.CreateCommand())
            {
                migrate.Transaction = transaction;
                migrate.CommandText = Migrations[version - 1] + $"\nINSERT INTO schema_version (version) VALUES ({version});";
                await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Applied state schema migration {Version}", version);
        }
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
