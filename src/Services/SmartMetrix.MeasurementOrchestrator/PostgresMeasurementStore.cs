using System.Text.Json;
using SmartMetrix.Contracts;
using Npgsql;
using NpgsqlTypes;
using SmartMetrix.Persistence;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed class PostgresMeasurementStore(PostgresDatabase database) : IMeasurementStore
{
    public async Task<IReadOnlyList<MeasurementProcess>> GetPendingCloudSyncAsync(CancellationToken cancellationToken = default)
    {
        await using var command = database.Source.CreateCommand("""
            SELECT payload::text FROM measurement.measurements
            WHERE status = 'Completed' AND payload->>'cloudQueuedAt' IS NULL
                AND payload->'pipeline'->>'resultUri' IS NOT NULL
            ORDER BY updated_at, id
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MeasurementProcess>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(Deserialize(reader.GetString(0)));
        return result;
    }

    public async Task<MeasurementProcess?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var command = database.Source.CreateCommand("SELECT payload::text FROM measurement.measurements WHERE id = $1");
        command.Parameters.AddWithValue(id);
        return await command.ExecuteScalarAsync(cancellationToken) is string json ? Deserialize(json) : null;
    }

    public Task<IReadOnlyList<MeasurementProcess>> GetUnfinishedAsync(CancellationToken cancellationToken = default) =>
        ListAsync(true, null, cancellationToken);

    public Task<IReadOnlyList<MeasurementProcess>> GetRecentAsync(int limit, bool activeOnly, CancellationToken cancellationToken = default) =>
        ListAsync(activeOnly, Math.Clamp(limit, 1, 200), cancellationToken);

    private async Task<IReadOnlyList<MeasurementProcess>> ListAsync(bool activeOnly, int? limit, CancellationToken ct)
    {
        await using var command = database.Source.CreateCommand("""
            SELECT payload::text FROM measurement.measurements
            WHERE NOT $1 OR status NOT IN ('Completed', 'Rejected', 'Failed')
            ORDER BY updated_at DESC, id LIMIT $2
            """);
        command.Parameters.AddWithValue(activeOnly);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)limit ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<MeasurementProcess>();
        while (await reader.ReadAsync(ct)) rows.Add(Deserialize(reader.GetString(0)));
        return rows;
    }

    public Task<bool> TryCreateAsync(MeasurementProcess measurement, CancellationToken cancellationToken = default) =>
        SaveAsync(measurement, null, cancellationToken);

    public Task<bool> TrySaveAsync(MeasurementProcess measurement, long expectedVersion, CancellationToken cancellationToken = default)
    {
        if (measurement.Version != expectedVersion + 1) throw new ArgumentException("A save must advance the measurement version by one.", nameof(measurement));
        return SaveAsync(measurement, expectedVersion, cancellationToken);
    }

    private async Task<bool> SaveAsync(MeasurementProcess value, long? expectedVersion, CancellationToken ct)
    {
        await using var connection = await database.Source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var sql = expectedVersion.HasValue ? """
            UPDATE measurement.measurements SET excavator_id=$2, coordinate_system_id=$3, status=$4,
                version=$5, requested_at=$6, updated_at=$7, is_test_data=$8, payload=$9
            WHERE id=$1 AND version=$10
            """ : """
            INSERT INTO measurement.measurements(id, excavator_id, coordinate_system_id, status, version, requested_at, updated_at, is_test_data, payload)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT (id) DO NOTHING
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(value.Id);
        command.Parameters.AddWithValue(value.ExcavatorId);
        command.Parameters.AddWithValue(value.CoordinateSystemId);
        command.Parameters.AddWithValue(value.Status.ToString());
        command.Parameters.AddWithValue(value.Version);
        command.Parameters.AddWithValue(value.RequestedAt.ToUniversalTime());
        command.Parameters.AddWithValue(value.UpdatedAt.ToUniversalTime());
        command.Parameters.AddWithValue(value.IsTestData);
        var payload = JsonSerializer.Serialize(value, PostgresDatabase.Json);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, payload);
        if (expectedVersion.HasValue) command.Parameters.AddWithValue(expectedVersion.Value);
        if (await command.ExecuteNonQueryAsync(ct) == 0) return false;
        await using var history = new NpgsqlCommand("INSERT INTO measurement.history(measurement_id, version, payload) VALUES ($1,$2,$3)", connection, transaction);
        history.Parameters.AddWithValue(value.Id);
        history.Parameters.AddWithValue(value.Version);
        history.Parameters.AddWithValue(NpgsqlDbType.Jsonb, payload);
        await history.ExecuteNonQueryAsync(ct);
        var envelope = EventEnvelope.Create(new MeasurementStateChanged(value.Id, value.Version, value.Status.ToString()), value.Id.ToString());
        await TransactionalEvents.EnqueueAsync(database.Schema, envelope.EventId, EventSubjects.For<MeasurementStateChanged>(),
            EventEnvelopeSerializer.Serialize(envelope), envelope.OccurredAt, connection, transaction, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static MeasurementProcess Deserialize(string json) => JsonSerializer.Deserialize<MeasurementProcess>(json, PostgresDatabase.Json)
        ?? throw new InvalidDataException("Invalid measurement in PostgreSQL.");
}
