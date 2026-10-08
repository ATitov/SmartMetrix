using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.CloudSyncService;

// Fetch only through the configured StorageService, never through URLs from JSON.
public sealed class MeasurementArtifactImporter(HttpClient storage, SyncQueueStore queue, IOptions<CloudSyncOptions> configured)
{
    public async Task<SyncItem> EnqueueAsync(EnqueueMeasurementRequest request, CancellationToken ct)
    {
        if (request.MeasurementId == Guid.Empty || request.RunId == Guid.Empty || request.Version < 1)
            throw new ArgumentException("Measurement, run and positive version are required.");
        var prefix = $"/measurements/{request.RunId:D}/";
        if (!TryPath(request.ResultUri, prefix, out var rootPath) || rootPath != "pipeline/result.json")
            throw new ArgumentException("Result must reference pipeline/result.json in the specified run.");
        if (await queue.FindAsync(request.MeasurementId, request.Version, ct) is { } existing)
        {
            if (!existing.Metadata.TryGetValue("runId", out var run) || run != request.RunId.ToString("D"))
                throw new ArgumentException("This measurement version already belongs to another run.");
            return existing;
        }
        var folder = Path.Combine(Path.GetFullPath(configured.Value.QueuePath), "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        try
        {
            var pending = new Queue<string>();
            pending.Enqueue(request.ResultUri);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var artifacts = new List<EnqueueArtifact>();
            while (pending.TryDequeue(out var reference))
            {
                ct.ThrowIfCancellationRequested();
                if (names.ContainsKey(reference)) continue;
                if (names.Count >= 10000) throw new InvalidDataException("Too many measurement artifacts.");
                if (!TryPath(reference, prefix, out var path)) throw new InvalidDataException("Invalid measurement artifact reference.");
                var name = names.Count.ToString("D5", CultureInfo.InvariantCulture) + "-" + Path.GetFileName(path);
                names.Add(reference, name);
                var local = Path.Combine(folder, name);
                files.Add(local);
                using var response = await storage.GetAsync($"v1/measurements/{request.RunId:D}/artifacts/download/{path}",
                    HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using (var output = File.Create(local)) await response.Content.CopyToAsync(output, ct);
                var json = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                artifacts.Add(new(local, json ? "application/json" : "application/octet-stream",
                    path.StartsWith("frames/", StringComparison.Ordinal) ? SyncPriority.RawFrame : SyncPriority.Result));
                if (json)
                {
                    await using var input = File.OpenRead(local);
                    using var document = await JsonDocument.ParseAsync(input, cancellationToken: ct);
                    if (path == rootPath && (document.RootElement.GetProperty("measurementId").GetGuid() != request.MeasurementId ||
                        document.RootElement.GetProperty("processingRunId").GetGuid() != request.RunId))
                        throw new InvalidDataException("Result identity does not match the requested measurement.");
                    Collect(document.RootElement);
                }
            }
            var index = Path.Combine(folder, "artifact-index.json");
            files.Add(index);
            await File.WriteAllBytesAsync(index, JsonSerializer.SerializeToUtf8Bytes(names), ct);
            artifacts.Add(new(index, "application/json"));
            return await queue.EnqueueAsync(new(request.MeasurementId, request.Version,
                new Dictionary<string, string> { ["runId"] = request.RunId.ToString("D"), ["resultUri"] = request.ResultUri }, artifacts), ct);

            void Collect(JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Object)
                    foreach (var property in element.EnumerateObject()) Collect(property.Value);
                else if (element.ValueKind == JsonValueKind.Array)
                    foreach (var child in element.EnumerateArray()) Collect(child);
                else if (element.ValueKind == JsonValueKind.String && element.GetString() is { } value && TryPath(value, prefix, out _))
                    pending.Enqueue(value);
            }
        }
        finally
        {
            foreach (var file in files) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    private static bool TryPath(string? reference, string prefix, out string path)
    {
        path = "";
        if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri) || uri.Scheme != "s3" ||
            !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        path = uri.AbsolutePath[prefix.Length..];
        var decoded = Uri.UnescapeDataString(path);
        return path.Length > 0 && !decoded.Contains("..", StringComparison.Ordinal) && !decoded.Contains('\\') &&
            !decoded.StartsWith('/') && !decoded.Contains('?') && !decoded.Contains('#');
    }
}
