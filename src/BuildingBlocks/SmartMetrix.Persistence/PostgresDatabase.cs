using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;

namespace SmartMetrix.Persistence;

/// <summary>Per-service database boundary. No connection string is logged or stored in data.</summary>
public sealed class PostgresDatabase : IDisposable
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web) { IncludeFields = true };
    public NpgsqlDataSource Source { get; }
    public string Schema { get; }

    public PostgresDatabase(string connectionString, string schema)
    {
        ValidateSchema(schema);
        Schema = schema;
        Source = NpgsqlDataSource.Create(connectionString);
    }

    internal static void ValidateSchema(string schema)
    {
        if (string.IsNullOrEmpty(schema) || schema.Length > 63 || schema.Any(c => c is not (>= 'a' and <= 'z') and not '_'))
            throw new ArgumentException("Invalid database schema.", nameof(schema));
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var connection = await Source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Serializes schema creation and migrations across starting service replicas.
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('smartmetrix:migrations', 0))", connection, transaction))
            await gate.ExecuteNonQueryAsync(ct);
        await using (var setup = new NpgsqlCommand($"""
            CREATE SCHEMA IF NOT EXISTS {Schema};
            CREATE TABLE IF NOT EXISTS {Schema}.schema_versions(version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
            """, connection, transaction)) await setup.ExecuteNonQueryAsync(ct);
        var assembly = typeof(PostgresDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(x => x.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            await using var exists = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {Schema}.schema_versions WHERE version = $1)", connection, transaction);
            exists.Parameters.AddWithValue(resource);
            if ((bool)(await exists.ExecuteScalarAsync(ct))!) continue;
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            var sql = (await reader.ReadToEndAsync(ct)).Replace("__schema__", Schema, StringComparison.Ordinal);
            await using (var migration = new NpgsqlCommand(sql, connection, transaction)) await migration.ExecuteNonQueryAsync(ct);
            await using var mark = new NpgsqlCommand($"INSERT INTO {Schema}.schema_versions(version) VALUES ($1)", connection, transaction);
            mark.Parameters.AddWithValue(resource);
            await mark.ExecuteNonQueryAsync(ct);
        }
        await RelationalRegistry.UpgradeAsync(Schema, connection, transaction, ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<T?> ReadAsync<T>(string key, CancellationToken ct = default)
    {
        if (RelationalRegistry.Handles(Schema, key))
        {
            await using var connection = await Source.OpenConnectionAsync(ct);
            // A multi-table registry must be read from one consistent snapshot.
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
            var snapshot = await RelationalRegistry.ReadAsync(Schema, key, connection, transaction, ct);
            return snapshot is null ? default : JsonSerializer.Deserialize<T>(snapshot, Json);
        }
        await using var command = Source.CreateCommand($"SELECT payload::text FROM {Schema}.documents WHERE key = $1");
        command.Parameters.AddWithValue(key);
        return await command.ExecuteScalarAsync(ct) is string json ? JsonSerializer.Deserialize<T>(json, Json) : default;
    }

    public async Task<StateSession<T>> OpenAsync<T>(string key, Func<T> create, CancellationToken ct = default)
    {
        var connection = await Source.OpenConnectionAsync(ct);
        try
        {
            var transaction = await connection.BeginTransactionAsync(ct);
            await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction);
            gate.Parameters.AddWithValue($"{Schema}:{key}");
            await gate.ExecuteNonQueryAsync(ct);
            string? json;
            if (RelationalRegistry.Handles(Schema, key)) json = await RelationalRegistry.ReadAsync(Schema, key, connection, transaction, ct);
            else
            {
                await using var read = new NpgsqlCommand($"SELECT payload::text FROM {Schema}.documents WHERE key = $1", connection, transaction);
                read.Parameters.AddWithValue(key);
                json = await read.ExecuteScalarAsync(ct) as string;
            }
            return new(this, key, connection, transaction, json is null ? create() : JsonSerializer.Deserialize<T>(json, Json)!, json is not null);
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public StateSession<T> Open<T>(string key, Func<T> create) => OpenAsync(key, create).GetAwaiter().GetResult();

    public async Task PutAsync<T>(string key, T value, CancellationToken ct = default)
    {
        await using var session = await OpenAsync(key, () => value, ct);
        session.Value = value;
        await session.CommitAsync(ct);
    }

    public void Dispose() => Source.Dispose();
}

/// <summary>A locked read/modify/write transaction. Dispose without Commit rolls back.</summary>
public sealed class StateSession<T>(PostgresDatabase database, string key, NpgsqlConnection connection,
    NpgsqlTransaction transaction, T value, bool exists) : IDisposable, IAsyncDisposable
{
    public T Value { get; set; } = value;
    public bool Exists { get; } = exists;

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (RelationalRegistry.Handles(database.Schema, key))
        {
            await RelationalRegistry.WriteAsync(database.Schema, key, JsonSerializer.Serialize(Value, PostgresDatabase.Json), connection, transaction, ct);
            await transaction.CommitAsync(ct);
            return;
        }
        await using var write = new NpgsqlCommand($"""
            INSERT INTO {database.Schema}.documents(key, payload) VALUES ($1, $2)
            ON CONFLICT (key) DO UPDATE SET payload = EXCLUDED.payload, revision = {database.Schema}.documents.revision + 1, updated_at = now()
            """, connection, transaction);
        write.Parameters.AddWithValue(key);
        write.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(Value, PostgresDatabase.Json));
        await write.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public void Commit() => CommitAsync().GetAwaiter().GetResult();
    public void Dispose() { transaction.Dispose(); connection.Dispose(); }
    public async ValueTask DisposeAsync() { await transaction.DisposeAsync(); await connection.DisposeAsync(); }
}

public static class PersistenceRegistration
{
    public static bool AddSmartMetrixPersistence(this IHostApplicationBuilder builder, string schema)
    {
        var provider = builder.Configuration["Persistence:Provider"] ?? "File";
        if (provider.Equals("File", StringComparison.OrdinalIgnoreCase)) return false;
        if (!provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Persistence:Provider must be File or Postgres.");
        var connection = builder.Configuration.GetConnectionString("SmartMetrix");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings:SmartMetrix is required for Postgres persistence.");
        builder.Services.AddSingleton(_ => new PostgresDatabase(connection, schema));
        builder.Services.AddHostedService<DatabaseInitializer>();
        builder.Services.AddHostedService<LegacyDataImporter>();
        builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"]);
        return true;
    }
}

internal sealed class DatabaseInitializer(PostgresDatabase database) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => database.MigrateAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class DatabaseHealthCheck(PostgresDatabase database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = database.Source.CreateCommand($"SELECT count(*) FROM {database.Schema}.schema_versions");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        { return HealthCheckResult.Unhealthy("PostgreSQL is unavailable."); }
    }
}
