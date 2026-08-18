using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace SmartMetrix.CloudSyncService;

public sealed class CloudSyncUploader(HttpClient httpClient, IOptions<CloudSyncOptions> options)
{
    private readonly CloudSyncOptions options = options.Value;

    public async Task<SyncItem> UploadAsync(
        SyncItem item,
        Func<SyncItem, Task> checkpoint,
        CancellationToken cancellationToken)
    {
        using var manifestRequest = new HttpRequestMessage(HttpMethod.Put, $"v1/measurements/{item.MeasurementId}/versions/{item.Version}")
        {
            Content = JsonContent.Create(new
            {
                item.MeasurementId,
                item.Version,
                item.Metadata,
                Artifacts = item.Artifacts.Select(x => new { x.Name, x.ContentType, x.Sha256, x.Size, x.Priority })
            })
        };
        manifestRequest.Headers.TryAddWithoutValidation("Idempotency-Key", item.Id.ToString("N"));
        using var manifestResponse = await httpClient.SendAsync(manifestRequest, cancellationToken);
        if (manifestResponse.StatusCode == HttpStatusCode.Conflict)
            throw new CloudVersionConflictException(await SafeBodyAsync(manifestResponse, cancellationToken));
        manifestResponse.EnsureSuccessStatusCode();

        foreach (var artifact in item.Artifacts.OrderByDescending(x => x.Priority))
        {
            var current = artifact;
            await VerifyLocalArtifactAsync(current, cancellationToken);
            while (current.UploadedBytes < current.Size)
            {
                await using var stream = new FileStream(current.SpoolPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                stream.Position = current.UploadedBytes;
                var count = (int)Math.Min(options.ChunkSizeBytes, current.Size - current.UploadedBytes);
                var buffer = new byte[count];
                await stream.ReadExactlyAsync(buffer, cancellationToken);
                var chunkHash = Convert.ToHexStringLower(SHA256.HashData(buffer));
                using var request = new HttpRequestMessage(HttpMethod.Put,
                    $"v1/measurements/{item.MeasurementId}/versions/{item.Version}/artifacts/{Uri.EscapeDataString(current.Name)}");
                request.Headers.TryAddWithoutValidation("Idempotency-Key", $"{item.Id:N}:{current.Name}:{current.UploadedBytes}");
                request.Headers.TryAddWithoutValidation("X-Content-SHA256", current.Sha256);
                request.Headers.TryAddWithoutValidation("X-Chunk-SHA256", chunkHash);
                request.Content = new ByteArrayContent(buffer);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(current.ContentType);
                request.Content.Headers.ContentRange = new ContentRangeHeaderValue(current.UploadedBytes, current.UploadedBytes + count - 1, current.Size);

                var started = DateTimeOffset.UtcNow;
                using var response = await httpClient.SendAsync(request, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Conflict)
                    throw new CloudVersionConflictException(await SafeBodyAsync(response, cancellationToken));
                response.EnsureSuccessStatusCode();
                if (!response.Headers.TryGetValues("X-Chunk-SHA256", out var values) ||
                    !string.Equals(values.FirstOrDefault(), chunkHash, StringComparison.OrdinalIgnoreCase))
                    throw new CloudChecksumException($"Cloud checksum mismatch for {current.Name} at offset {current.UploadedBytes}.");

                current = current with { UploadedBytes = current.UploadedBytes + count };
                item = item with { Artifacts = item.Artifacts.Select(x => x.Name == current.Name ? current : x).ToArray() };
                await checkpoint(item);
                await ThrottleAsync(count, started, cancellationToken);
            }
        }

        using var completeRequest = new HttpRequestMessage(HttpMethod.Post,
            $"v1/measurements/{item.MeasurementId}/versions/{item.Version}/complete");
        completeRequest.Headers.TryAddWithoutValidation("Idempotency-Key", item.Id.ToString("N"));
        using var completeResponse = await httpClient.SendAsync(completeRequest, cancellationToken);
        if (completeResponse.StatusCode == HttpStatusCode.Conflict)
            throw new CloudVersionConflictException(await SafeBodyAsync(completeResponse, cancellationToken));
        completeResponse.EnsureSuccessStatusCode();
        return item;
    }

    private static async Task VerifyLocalArtifactAsync(SyncArtifact artifact, CancellationToken ct)
    {
        await using var stream = File.OpenRead(artifact.SpoolPath);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (stream.Length != artifact.Size || !string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CloudChecksumException($"Local spool artifact {artifact.Name} failed checksum validation.");
    }

    private async Task ThrottleAsync(int bytes, DateTimeOffset started, CancellationToken ct)
    {
        if (options.BandwidthLimitBytesPerSecond <= 0) return;
        var required = TimeSpan.FromSeconds((double)bytes / options.BandwidthLimitBytesPerSecond);
        var delay = required - (DateTimeOffset.UtcNow - started);
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
    }

    private static async Task<string> SafeBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "Cloud rejected the upload." : body;
    }
}
