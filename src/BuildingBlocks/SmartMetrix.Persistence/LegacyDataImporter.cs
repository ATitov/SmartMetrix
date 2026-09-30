using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;

namespace SmartMetrix.Persistence;

/// <summary>Explicit, restartable import. Source files are never changed or removed.</summary>
public sealed class LegacyDataImporter(PostgresDatabase database, IConfiguration configuration, IHostEnvironment environment) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("Persistence:ImportLegacyFiles")) return;
        await using var marker = await database.OpenAsync("legacy-import-v1", () => false, cancellationToken);
        if (marker.Value) return;
        switch (database.Schema)
        {
            case "measurement":
                var directory = Path.Combine(environment.ContentRootPath, "data", "measurements");
                if (Directory.Exists(directory))
                    foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
                        await ImportMeasurementAsync(await ReadAsync(file, cancellationToken), cancellationToken);
                break;
            case "operator":
                await ImportDocumentAsync("users", ConfigPath("OperatorApi:UserStorePath", "data/users.json"), cancellationToken);
                await ImportDocumentAsync("reviews", ConfigPath("Workstations:ReviewPath", "data/workstation-reviews.json"), cancellationToken);
                var configFile = ConfigPath("OperatorApi:RuntimeConfigPath", "data/runtime-config.json");
                if (File.Exists(configFile)) await ImportValueAsync("runtime-config-history",
                    JsonSerializer.SerializeToElement(new[] { new { at = File.GetLastWriteTimeUtc(configFile), values = await ReadAsync(configFile, cancellationToken) } }), cancellationToken);
                var auditFile = ConfigPath("OperatorApi:AuditPath", "data/operator-audit.jsonl");
                if (File.Exists(auditFile))
                {
                    var index = 0;
                    foreach (var line in await File.ReadAllLinesAsync(auditFile, cancellationToken))
                    {
                        index++;
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        var entry = JsonSerializer.Deserialize<JsonElement>(line);
                        await using var command = database.Source.CreateCommand($"INSERT INTO {database.Schema}.events(occurred_at,payload,import_key) VALUES($1,$2,$3) ON CONFLICT(import_key) DO NOTHING");
                        command.Parameters.AddWithValue(entry.GetProperty("occurredAt").GetDateTimeOffset().ToUniversalTime());
                        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, entry.GetRawText());
                        command.Parameters.AddWithValue($"legacy:{index}");
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }
                }
                break;
            case "camera":
                var captures = Path.Combine(environment.ContentRootPath, "data", "capture-receipts");
                if (Directory.Exists(captures))
                {
                    foreach (var file in Directory.EnumerateFiles(captures, "*.json"))
                    {
                        var response = await ReadAsync(file, cancellationToken);
                        await ImportValueAsync($"capture:{Guid.Parse(Path.GetFileNameWithoutExtension(file)):N}",
                            JsonSerializer.SerializeToElement(new { calibrationId = response.GetProperty("calibrationId"), response }), cancellationToken);
                    }
                    foreach (var file in Directory.EnumerateFiles(captures, "*.pending"))
                    {
                        if (File.Exists(Path.ChangeExtension(file, ".json"))) continue;
                        var calibration = await File.ReadAllTextAsync(file, cancellationToken);
                        await ImportValueAsync($"capture:{Guid.Parse(Path.GetFileNameWithoutExtension(file)):N}",
                            JsonSerializer.SerializeToElement(new { calibrationId = calibration.Length == 0 ? null : calibration, response = (object?)null }), cancellationToken);
                    }
                }
                break;
            case "cloud_sync":
                // Matches the legacy store: QueuePath is relative to the process working directory.
                var queue = Path.GetFullPath(configuration["CloudSync:QueuePath"] ?? "data/cloud-sync");
                var itemsDirectory = Path.Combine(queue, "items");
                var items = new List<JsonElement>();
                if (Directory.Exists(itemsDirectory))
                    foreach (var file in Directory.EnumerateFiles(itemsDirectory, "*.json")) items.Add(await ReadAsync(file, cancellationToken));
                var auditPath = Path.Combine(queue, "audit.jsonl");
                var audit = File.Exists(auditPath) ? (await File.ReadAllLinesAsync(auditPath, cancellationToken))
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => JsonSerializer.Deserialize<JsonElement>(x)).ToArray() : [];
                if (items.Count > 0 || audit.Length > 0)
                    await ImportValueAsync("queue", JsonSerializer.SerializeToElement(new { items, audit }), cancellationToken);
                break;
        }
        marker.Value = true;
        await marker.CommitAsync(cancellationToken);
    }

    private async Task ImportMeasurementAsync(JsonElement value, CancellationToken ct)
    {
        var id = value.GetProperty("id").GetGuid();
        await using var connection = await database.Source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO measurement.measurements(id,excavator_id,coordinate_system_id,status,version,requested_at,updated_at,is_test_data,payload)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)
            ON CONFLICT(id) DO UPDATE SET id=EXCLUDED.id WHERE measurement.measurements.payload=EXCLUDED.payload
            """, connection, transaction);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(value.GetProperty("excavatorId").GetString()!);
        command.Parameters.AddWithValue(value.GetProperty("coordinateSystemId").GetString()!);
        command.Parameters.AddWithValue(value.GetProperty("status").GetString()!);
        command.Parameters.AddWithValue(value.GetProperty("version").GetInt64());
        command.Parameters.AddWithValue(value.GetProperty("requestedAt").GetDateTimeOffset().ToUniversalTime());
        command.Parameters.AddWithValue(value.GetProperty("updatedAt").GetDateTimeOffset().ToUniversalTime());
        command.Parameters.AddWithValue(value.TryGetProperty("isTestData", out var test) && test.GetBoolean());
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, value.GetRawText());
        if (await command.ExecuteNonQueryAsync(ct) == 0) throw new InvalidOperationException($"Legacy measurement {id} conflicts with PostgreSQL. Import into an empty database before starting writers.");
        await using var history = new NpgsqlCommand("INSERT INTO measurement.history(measurement_id,version,payload) VALUES($1,$2,$3) ON CONFLICT DO NOTHING", connection, transaction);
        history.Parameters.AddWithValue(id);
        history.Parameters.AddWithValue(value.GetProperty("version").GetInt64());
        history.Parameters.AddWithValue(NpgsqlDbType.Jsonb, value.GetRawText());
        await history.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task ImportDocumentAsync(string key, string path, CancellationToken ct)
    {
        if (File.Exists(path)) await ImportValueAsync(key, await ReadAsync(path, ct), ct);
    }

    private async Task ImportValueAsync(string key, JsonElement value, CancellationToken ct)
    {
        await using var session = await database.OpenAsync(key, () => value, ct);
        if (session.Exists && !JsonElement.DeepEquals(session.Value, value))
            throw new InvalidOperationException($"Legacy data conflicts with PostgreSQL document '{key}'. Import into an empty database before starting writers.");
        session.Value = value;
        await session.CommitAsync(ct);
    }

    private string ConfigPath(string name, string fallback) => Path.GetFullPath(configuration[name] ?? fallback, environment.ContentRootPath);
    private static async Task<JsonElement> ReadAsync(string path, CancellationToken ct) => JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(path, ct));
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
