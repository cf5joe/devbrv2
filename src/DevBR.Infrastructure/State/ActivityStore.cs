using DevBR.Domain;
using Microsoft.Extensions.Logging;

namespace DevBR.Infrastructure.State;

public sealed record ActivityRecord(long Id, DateTimeOffset Timestamp, EventSeverity Severity, string Category, string Message, string? Detail);

/// <summary>User-visible history of what DevBR did. Messages must already be free of sensitive values.</summary>
public sealed class ActivityStore(StateDatabase database, ILogger<ActivityStore> logger)
{
    public event EventHandler<ActivityRecord>? Added;

    public async Task AddAsync(EventSeverity severity, string category, string message, string? detail = null, CancellationToken cancellationToken = default)
    {
        var timestamp = DateTimeOffset.UtcNow;
        long id;
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO activity (timestamp_utc, severity, category, message, detail)
                VALUES ($timestamp, $severity, $category, $message, $detail)
                RETURNING id;
                """;
            command.Parameters.AddWithValue("$timestamp", timestamp.ToString("O"));
            command.Parameters.AddWithValue("$severity", severity.ToString());
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$message", message);
            command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Activity history is a convenience; failing to record it must never break the operation itself.
            logger.LogWarning(ex, "Could not record activity entry.");
            return;
        }

        Added?.Invoke(this, new ActivityRecord(id, timestamp, severity, category, message, detail));
    }

    public async Task<IReadOnlyList<ActivityRecord>> ListRecentAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, timestamp_utc, severity, category, message, detail FROM activity ORDER BY id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);

        var records = new List<ActivityRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(new ActivityRecord(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                Enum.TryParse<EventSeverity>(reader.GetString(2), out var severity) ? severity : EventSeverity.Information,
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return records;
    }
}
