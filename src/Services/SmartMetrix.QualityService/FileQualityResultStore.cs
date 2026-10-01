using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.QualityService;

// One immutable result per processing run; the orchestrator uses runId as measurementId here.
public sealed class FileQualityResultStore(IOptions<QualityOptions> configured, IHostEnvironment environment) : IQualityResultStore
{
    private readonly string root = Path.GetFullPath(configured.Value.ResultDirectory, environment.ContentRootPath);
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Save(QualityResult result)
    {
        lock (gate)
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, result.MeasurementId.ToString("N") + ".json");
            if (File.Exists(path))
            {
                var previous = JsonSerializer.Deserialize<QualityResult>(File.ReadAllText(path), Json);
                if (JsonSerializer.Serialize(previous! with { AssessedAt = result.AssessedAt }, Json) != JsonSerializer.Serialize(result, Json))
                    throw new InvalidOperationException("A different quality result already exists for this processing run.");
                return;
            }
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, result, Json);
                    stream.Flush(true);
                }
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public bool TryGet(Guid measurementId, out QualityResult? result)
    {
        lock (gate)
        {
            var path = Path.Combine(root, measurementId.ToString("N") + ".json");
            result = File.Exists(path) ? JsonSerializer.Deserialize<QualityResult>(File.ReadAllText(path), Json) : null;
            return result is not null;
        }
    }
}
