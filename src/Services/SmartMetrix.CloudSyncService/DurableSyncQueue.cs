using SmartMetrix.Persistence;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.CloudSyncService;

public sealed class SyncQueueStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly PostgresDatabase? database;
    private readonly string root;
    private readonly string itemsPath;
    private readonly string spoolPath;
    private readonly string auditPath;

    public SyncQueueStore(IOptions<CloudSyncOptions> options, PostgresDatabase? database = null)
    {
        this.database = database;
        root = Path.GetFullPath(options.Value.QueuePath);
        itemsPath = Path.Combine(root, "items");
        spoolPath = Path.Combine(root, "spool");
        auditPath = Path.Combine(root, "audit.jsonl");
        Directory.CreateDirectory(itemsPath);
        Directory.CreateDirectory(spoolPath);
    }

    public async Task<SyncItem> EnqueueAsync(EnqueueSyncRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MeasurementId == Guid.Empty || request.Version < 1 || request.Artifacts.Count == 0)
            throw new ArgumentException("MeasurementId, positive Version and at least one artifact are required.");

        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var session = database is null ? null : await database.OpenAsync("queue", () => new StoredState([], []), cancellationToken);
            var id = DeterministicId(request.MeasurementId, request.Version);
            var itemFile = ItemFile(id);
            if (session?.Value.Items.FirstOrDefault(x => x.Id == id) is { } existing) return existing;
            if (session is null && File.Exists(itemFile)) return await ReadAsync(itemFile, cancellationToken);

            var itemSpool = Path.Combine(spoolPath, id.ToString("N"));
            Directory.CreateDirectory(itemSpool);
            var artifacts = new List<SyncArtifact>();
            foreach (var source in request.Artifacts.OrderByDescending(x => x.Priority))
            {
                var sourcePath = Path.GetFullPath(source.Path);
                if (!File.Exists(sourcePath)) throw new FileNotFoundException("Sync artifact was not found.", sourcePath);
                var name = Path.GetFileName(sourcePath);
                var destination = Path.Combine(itemSpool, $"{artifacts.Count:D4}-{name}");
                await CopyDurablyAsync(sourcePath, destination, cancellationToken);
                await using var stream = File.OpenRead(destination);
                var checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                artifacts.Add(new(name, destination, source.ContentType, checksum, stream.Length, source.Priority));
            }

            var now = DateTimeOffset.UtcNow;
            var item = new SyncItem(id, request.MeasurementId, request.Version,
                request.Metadata ?? new Dictionary<string, string>(), artifacts, SyncState.Pending, 0, now, now);
            if (session is not null)
            {
                session.Value.Items.Add(item);
                session.Value.Audit.Add(new(now, item.Id, item.MeasurementId, "enqueued", null));
                await session.CommitAsync(cancellationToken);
            }
            else
            {
                await WriteAsync(item, cancellationToken);
                await AuditAsync(item, "enqueued", null, cancellationToken);
            }
            return item;
        }
        finally { gate.Release(); }
    }

    public async Task<SyncItem?> GetNextAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (database is not null)
            {
                await using var query = database.Source.CreateCommand("""
                    SELECT payload::text FROM cloud_sync.sync_items
                    WHERE state IN(0,1,2) AND next_attempt_at <= $1
                    ORDER BY EXISTS(SELECT 1 FROM jsonb_array_elements(payload->'artifacts') a WHERE (a->>'priority')::integer=100) DESC,
                        created_at,key LIMIT 1
                    """);
                query.Parameters.AddWithValue(now.ToUniversalTime());
                return await query.ExecuteScalarAsync(cancellationToken) is string json ? JsonSerializer.Deserialize<SyncItem>(json, JsonOptions) : null;
            }
            var items = await ReadItemsAsync(cancellationToken);
            return items.Where(x => (x.State is SyncState.Pending or SyncState.Retry or SyncState.Uploading) && x.NextAttemptAt <= now)
                .OrderByDescending(x => x.Artifacts.Any(a => a.Priority == SyncPriority.Result))
                .ThenBy(x => x.CreatedAt).FirstOrDefault();
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(SyncItem item, string action, string? detail, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (database is not null)
            {
                await using var session = await database.OpenAsync("queue", () => new StoredState([], []), cancellationToken);
                var index = session.Value.Items.FindIndex(x => x.Id == item.Id);
                if (index < 0) throw new KeyNotFoundException("Sync item does not exist.");
                session.Value.Items[index] = item;
                session.Value.Audit.Add(new(DateTimeOffset.UtcNow, item.Id, item.MeasurementId, action, detail));
                await session.CommitAsync(cancellationToken);
            }
            else { await WriteAsync(item, cancellationToken); await AuditAsync(item, action, detail, cancellationToken); }
        }
        finally { gate.Release(); }
    }

    public async Task<SyncQueueStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadItemsAsync(cancellationToken);
            var active = all.Where(x => x.State is SyncState.Pending or SyncState.Retry or SyncState.Uploading).ToArray();
            return new(active.LongLength, active.Sum(x => x.Artifacts.Sum(a => Math.Max(0, a.Size - a.UploadedBytes))),
                all.Where(x => x.CompletedAt.HasValue).MaxBy(x => x.CompletedAt)?.CompletedAt,
                all.LongCount(x => x.State == SyncState.Conflict));
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<SyncAuditEntry>> GetAuditAsync(int take, CancellationToken cancellationToken)
    {
        if (database is not null)
        {
            await using var query = database.Source.CreateCommand("SELECT payload::text FROM cloud_sync.audit_entries ORDER BY ordinal DESC LIMIT $1");
            query.Parameters.AddWithValue(Math.Clamp(take, 1, 1000));
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            var entries = new List<SyncAuditEntry>();
            while (await reader.ReadAsync(cancellationToken)) entries.Add(JsonSerializer.Deserialize<SyncAuditEntry>(reader.GetString(0), JsonOptions)!);
            return entries;
        }
        if (!File.Exists(auditPath)) return [];
        var lines = await File.ReadAllLinesAsync(auditPath, cancellationToken);
        return lines.Reverse().Take(Math.Clamp(take, 1, 1000))
            .Select(x => JsonSerializer.Deserialize<SyncAuditEntry>(x, JsonOptions)!).ToArray();
    }

    public void DeleteSpool(SyncItem item)
    {
        foreach (var artifact in item.Artifacts)
            if (File.Exists(artifact.SpoolPath)) File.Delete(artifact.SpoolPath);
        var directory = Path.Combine(spoolPath, item.Id.ToString("N"));
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    public sealed record StoredState(List<SyncItem> Items, List<SyncAuditEntry> Audit);

    public async Task<StateSession<bool>?> AcquireWorkerAsync(CancellationToken ct) => database is null
        ? null : await database.OpenAsync("worker-lock", () => true, ct);

    private async Task<List<SyncItem>> ReadItemsAsync(CancellationToken ct)
    {
        if (database is not null) return (await database.ReadAsync<StoredState>("queue", ct))?.Items ?? [];
        var rows = new List<SyncItem>();
        foreach (var file in Directory.EnumerateFiles(itemsPath, "*.json")) rows.Add(await ReadAsync(file, ct));
        return rows;
    }

    private string ItemFile(Guid id) => Path.Combine(itemsPath, $"{id:N}.json");
    private static Guid DeterministicId(Guid measurementId, long version) => new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{measurementId:N}:{version}"))[..16]);
    private static async Task<SyncItem> ReadAsync(string path, CancellationToken ct) =>
        JsonSerializer.Deserialize<SyncItem>(await File.ReadAllTextAsync(path, ct), JsonOptions) ?? throw new InvalidDataException($"Invalid queue item: {path}");
    private async Task WriteAsync(SyncItem item, CancellationToken ct)
    {
        var target = ItemFile(item.Id); var temp = target + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(item, JsonOptions), ct);
        File.Move(temp, target, true);
    }
    private async Task AuditAsync(SyncItem item, string action, string? detail, CancellationToken ct) =>
        await File.AppendAllTextAsync(auditPath, JsonSerializer.Serialize(new SyncAuditEntry(DateTimeOffset.UtcNow, item.Id, item.MeasurementId, action, detail), JsonOptions) + Environment.NewLine, ct);
    private static async Task CopyDurablyAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // Enqueue holds the gate and returns before copying if the item manifest exists.
        // An unpublished spool may remain after a failed/cancelled enqueue or process restart.
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, ct); await output.FlushAsync(ct);
    }

    public void Dispose() => gate.Dispose();
}
