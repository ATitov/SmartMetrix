using SmartMetrix.Persistence;
using NpgsqlTypes;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.ApiGateway;

public interface IAuditStore
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditEntry>> ReadAsync(CancellationToken cancellationToken);
}

public sealed class JsonAuditStore(IHostEnvironment environment, IOptions<OperatorApiOptions> options, PostgresDatabase? database = null) : IAuditStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.GetFullPath(options.Value.AuditPath, environment.ContentRootPath);

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        if (database is not null)
        {
            await using var command = database.Source.CreateCommand("INSERT INTO operator.events(occurred_at, payload) VALUES($1,$2)");
            command.Parameters.AddWithValue(entry.OccurredAt.ToUniversalTime());
            command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(entry, PostgresDatabase.Json));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AuditEntry>> ReadAsync(CancellationToken cancellationToken)
    {
        if (database is not null)
        {
            await using var command = database.Source.CreateCommand("SELECT payload::text FROM operator.events ORDER BY occurred_at DESC, id DESC LIMIT 200");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<AuditEntry>();
            while (await reader.ReadAsync(cancellationToken)) rows.Add(JsonSerializer.Deserialize<AuditEntry>(reader.GetString(0), PostgresDatabase.Json)!);
            return rows;
        }
        if (!File.Exists(_path)) return [];
        var result = new List<AuditEntry>();
        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
            if (JsonSerializer.Deserialize<AuditEntry>(line, JsonOptions) is { } entry) result.Add(entry);
        result.Reverse();
        return result.Take(200).ToArray();
    }

    public void Dispose() => _gate.Dispose();
}
