using System.Globalization;
using DevBR.Application.Restore;
using Microsoft.Data.Sqlite;

namespace DevBR.Infrastructure.State;

/// <summary>
/// The restore journal in the state database. Every write is its own committed transaction with full
/// synchronous durability, so an intent is on disk before the side effect it describes begins.
/// </summary>
public sealed class SqliteRestoreJournal(string connectionString) : IRestoreJournal
{
    public SqliteRestoreJournal(StateDatabase database)
        : this(database.ConnectionString)
    {
    }

    public void CreateJob(Guid jobId, string kind, string summary)
        => Execute("""
            INSERT INTO jobs (id, kind, state, created_utc, updated_utc, summary)
            VALUES ($id, $kind, $state, $now, $now, $summary);
            """, ("$id", Id(jobId)), ("$kind", kind), ("$state", JobStates.Running), ("$now", Now()), ("$summary", summary));

    public void UpdateJob(Guid jobId, string state, string? summary = null)
        => Execute("""
            UPDATE jobs SET state = $state, updated_utc = $now, summary = COALESCE($summary, summary) WHERE id = $id;
            """, ("$id", Id(jobId)), ("$state", state), ("$now", Now()), ("$summary", summary));

    public void RecordIntent(Guid jobId, string operationId, int sequence, string intent)
        => Execute("""
            INSERT INTO job_operations (job_id, operation_id, sequence, intent, outcome, intent_utc, outcome_utc)
            VALUES ($job, $op, $seq, $intent, NULL, $now, NULL)
            ON CONFLICT (job_id, operation_id) DO UPDATE SET intent = excluded.intent, sequence = excluded.sequence,
                intent_utc = excluded.intent_utc, outcome = NULL, outcome_utc = NULL;
            """, ("$job", Id(jobId)), ("$op", operationId), ("$seq", sequence), ("$intent", intent), ("$now", Now()));

    public void RecordOutcome(Guid jobId, string operationId, string outcome)
        => Execute("""
            UPDATE job_operations SET outcome = $outcome, outcome_utc = $now WHERE job_id = $job AND operation_id = $op;
            """, ("$job", Id(jobId)), ("$op", operationId), ("$outcome", outcome), ("$now", Now()));

    public JournalJob? GetJob(Guid jobId)
        => Query("SELECT id, kind, state, created_utc, updated_utc, summary FROM jobs WHERE id = $id;", ReadJob, ("$id", Id(jobId))).FirstOrDefault();

    public IReadOnlyList<JournalJob> ListJobs(string kind, int limit)
        => Query("SELECT id, kind, state, created_utc, updated_utc, summary FROM jobs WHERE kind = $kind ORDER BY created_utc DESC LIMIT $limit;",
            ReadJob, ("$kind", kind), ("$limit", limit));

    public IReadOnlyList<JournalRecord> Read(Guid jobId)
        => Query("""
            SELECT operation_id, sequence, intent, outcome, intent_utc, outcome_utc FROM job_operations WHERE job_id = $job ORDER BY sequence, intent_utc;
            """,
            r => new JournalRecord(r.GetString(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), Time(r.GetString(4)),
                r.IsDBNull(5) ? null : Time(r.GetString(5))),
            ("$job", Id(jobId)));

    private static JournalJob ReadJob(SqliteDataReader r)
        => new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), Time(r.GetString(3)), Time(r.GetString(4)), r.IsDBNull(5) ? null : r.GetString(5));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous = FULL; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        command.ExecuteNonQuery();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(read(reader));
        }

        return results;
    }

    private static void Bind(SqliteCommand command, (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static string Id(Guid id) => id.ToString("D");

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
