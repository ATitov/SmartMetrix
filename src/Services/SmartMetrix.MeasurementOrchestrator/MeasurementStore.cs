using System.Collections.Concurrent;
using System.Text.Json;

namespace SmartMetrix.MeasurementOrchestrator;

public interface IMeasurementStore
{
    Task<MeasurementProcess?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MeasurementProcess>> GetUnfinishedAsync(CancellationToken cancellationToken = default);
    Task<bool> TryCreateAsync(MeasurementProcess measurement, CancellationToken cancellationToken = default);
    Task<bool> TrySaveAsync(MeasurementProcess measurement, long expectedVersion, CancellationToken cancellationToken = default);
}

public sealed class JsonMeasurementStore(IHostEnvironment environment) : IMeasurementStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _directory = Path.Combine(environment.ContentRootPath, "data", "measurements");
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<MeasurementProcess?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<MeasurementProcess>(stream, JsonOptions, cancellationToken);
    }

    public async Task<IReadOnlyList<MeasurementProcess>> GetUnfinishedAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var result = new List<MeasurementProcess>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            await using var stream = File.OpenRead(path);
            var item = await JsonSerializer.DeserializeAsync<MeasurementProcess>(stream, JsonOptions, cancellationToken);
            if (item is not null && item.Status is not (SmartMetrix.Domain.MeasurementStatus.Completed or SmartMetrix.Domain.MeasurementStatus.Rejected or SmartMetrix.Domain.MeasurementStatus.Failed)) result.Add(item);
        }
        return result;
    }

    public async Task<bool> TryCreateAsync(MeasurementProcess measurement, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(measurement.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(PathFor(measurement.Id))) return false;
            await WriteAsync(measurement, cancellationToken);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> TrySaveAsync(MeasurementProcess measurement, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(measurement.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await GetAsync(measurement.Id, cancellationToken);
            if (current?.Version != expectedVersion) return false;
            await WriteAsync(measurement, cancellationToken);
            return true;
        }
        finally { gate.Release(); }
    }

    private string PathFor(Guid id) => Path.Combine(_directory, $"{id:N}.json");

    private async Task WriteAsync(MeasurementProcess measurement, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        var target = PathFor(measurement.Id);
        var temporary = target + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, measurement, JsonOptions, cancellationToken);
        File.Move(temporary, target, true);
    }
}
