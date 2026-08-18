using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.ApiGateway;

public interface IAuditStore
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuditEntry>> ReadAsync(CancellationToken cancellationToken);
}

public sealed class JsonAuditStore(IHostEnvironment environment, IOptions<OperatorApiOptions> options) : IAuditStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.GetFullPath(options.Value.AuditPath, environment.ContentRootPath);

    public async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
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
        if (!File.Exists(_path)) return [];
        var result = new List<AuditEntry>();
        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
            if (JsonSerializer.Deserialize<AuditEntry>(line, JsonOptions) is { } entry) result.Add(entry);
        result.Reverse();
        return result.Take(200).ToArray();
    }

    public void Dispose() => _gate.Dispose();
}
