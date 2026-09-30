using System.Text.Json;
using NpgsqlTypes;
using SmartMetrix.Persistence;

namespace SmartMetrix.MeasurementOrchestrator;

/// <summary>Immutable structured stage results; binary artifacts continue to live in MinIO.</summary>
public sealed class PostgresStageStore(PostgresDatabase database)
{
    public async Task<JsonElement?> ReadAsync(Guid runId, string stage, CancellationToken ct)
    {
        await using var command = database.Source.CreateCommand("SELECT payload::text FROM measurement.stages WHERE run_id=$1 AND stage=$2");
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(stage);
        return await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<JsonElement>(json) : null;
    }

    public async Task SaveAsync(Guid measurementId, Guid runId, string stage, string uri, JsonElement payload, CancellationToken ct)
    {
        await using var command = database.Source.CreateCommand("""
            INSERT INTO measurement.stages(measurement_id, run_id, stage, artifact_uri, payload) VALUES($1,$2,$3,$4,$5)
            ON CONFLICT (run_id, stage) DO UPDATE SET artifact_uri=EXCLUDED.artifact_uri
            WHERE measurement.stages.measurement_id=EXCLUDED.measurement_id AND measurement.stages.payload=EXCLUDED.payload
                AND measurement.stages.artifact_uri=EXCLUDED.artifact_uri
            """);
        command.Parameters.AddWithValue(measurementId);
        command.Parameters.AddWithValue(runId);
        command.Parameters.AddWithValue(stage);
        command.Parameters.AddWithValue(uri);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, payload.GetRawText());
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("A persisted stage cannot be overwritten with different data.");
    }
}
