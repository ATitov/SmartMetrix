using Npgsql;

namespace SmartMetrix.Persistence;

public static class TransactionalEvents
{
    /// <summary>Call with the SAME transaction that writes domain state.</summary>
    public static async Task EnqueueAsync(string schema, Guid eventId, string subject, byte[] payload, DateTimeOffset occurredAt,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct = default)
    {
        PostgresDatabase.ValidateSchema(schema);
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {schema}.outbox(event_id,subject,payload,created_at) VALUES($1,$2,$3,$4)
            ON CONFLICT(event_id) DO UPDATE SET event_id=EXCLUDED.event_id
            WHERE {schema}.outbox.subject=EXCLUDED.subject AND {schema}.outbox.payload=EXCLUDED.payload
            """, connection, transaction);
        command.Parameters.AddWithValue(eventId);
        command.Parameters.AddWithValue(subject);
        command.Parameters.AddWithValue(payload);
        command.Parameters.AddWithValue(occurredAt.ToUniversalTime());
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Event identity was reused for different content.");
    }
}

/// <summary>Inbox marker and database side effects commit together. A crash releases
/// the transaction lock and rolls back both, so no expiring lease or stale owner can lose an event.</summary>
public sealed class PostgresInboxProcessor(PostgresDatabase database)
{
    public async Task<bool> ProcessAsync(string consumer, Guid eventId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> handler, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        if (eventId == Guid.Empty) throw new ArgumentException("Event id is required.", nameof(eventId));
        await using var connection = await database.Source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction);
        gate.Parameters.AddWithValue($"{database.Schema}:inbox:{consumer}:{eventId:N}");
        await gate.ExecuteNonQueryAsync(ct);
        await using var mark = new NpgsqlCommand($"INSERT INTO {database.Schema}.inbox(consumer,event_id) VALUES($1,$2) ON CONFLICT DO NOTHING", connection, transaction);
        mark.Parameters.AddWithValue(consumer);
        mark.Parameters.AddWithValue(eventId);
        if (await mark.ExecuteNonQueryAsync(ct) == 0) return false;
        await handler(connection, transaction, ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
