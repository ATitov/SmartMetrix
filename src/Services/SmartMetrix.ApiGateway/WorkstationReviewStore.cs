using SmartMetrix.Persistence;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.ApiGateway;

public sealed class WorkstationReviewStore(IOptions<WorkstationOptions> options, IHostEnvironment environment, PostgresDatabase? database = null) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path = Path.GetFullPath(options.Value.ReviewPath, environment.ContentRootPath);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ReviewRecord>> ListAsync(string scope, Guid id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).Where(x => x.ScopeId == scope && x.MeasurementId == id).ToArray(); }
        finally { gate.Release(); }
    }

    public async Task<ReviewRecord> SaveAsync(ReviewRecord record, Func<Task<(long Version, string Status)>> current, CancellationToken ct)
    {
        WorkstationValidation.Command(record.CommandId, record.ResultVersion, record.Reason);
        if (record.Decision is not ("Approved" or "NeedsRevision")) throw new WorkstationApiException(400, "InvalidDecision");
        await gate.WaitAsync(ct);
        try
        {
            await using var session = database is null ? null : await database.OpenAsync("reviews", () => new List<ReviewRecord>(), ct);
            var rows = session is null ? await LoadAsync(ct) : session.Value;
            if (rows.FirstOrDefault(x => x.CommandId == record.CommandId) is { } existing)
            {
                if (existing.ScopeId != record.ScopeId || existing.MeasurementId != record.MeasurementId ||
                    existing.ResultVersion != record.ResultVersion || existing.Decision != record.Decision ||
                    existing.Reason != record.Reason || existing.Actor != record.Actor)
                    throw new WorkstationApiException(409, "CommandConflict");
                return existing;
            }
            var state = await current();
            if (state.Version != record.ResultVersion || state.Status != "Completed") throw new WorkstationApiException(409, "ResultVersionConflict");
            rows.Add(record);
            if (session is not null) { await session.CommitAsync(ct); return record; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(rows, Json), ct);
            File.Move(temporary, path, true);
            return record;
        }
        finally { gate.Release(); }
    }

    private async Task<List<ReviewRecord>> LoadAsync(CancellationToken ct) => database is not null
        ? await database.ReadAsync<List<ReviewRecord>>("reviews", ct) ?? [] : File.Exists(path)
        ? JsonSerializer.Deserialize<List<ReviewRecord>>(await File.ReadAllTextAsync(path, ct), Json) ?? [] : [];
    public void Dispose() => gate.Dispose();
}
