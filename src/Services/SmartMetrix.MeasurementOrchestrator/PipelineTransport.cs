using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed class PipelineTransport(HttpClient client, IOptions<PipelineOptions> options)
{
    public async Task<JsonElement> GetAsync(string baseUrl, string path, Guid measurementId, CancellationToken ct) =>
        await SendJsonAsync(HttpMethod.Get, baseUrl, path, null, measurementId, ct);

    public async Task<JsonElement> PostAsync(string baseUrl, string path, object body, Guid measurementId, CancellationToken ct) =>
        await SendJsonAsync(HttpMethod.Post, baseUrl, path, body, measurementId, ct);

    private async Task<JsonElement> SendJsonAsync(HttpMethod method, string baseUrl, string path, object? body, Guid id, CancellationToken ct)
    {
        using var request = Create(method, baseUrl, path, id);
        if (body is not null) request.Content = JsonContent.Create(body, options: PipelineJson.Options);
        using var response = await client.SendAsync(request, ct);
        await CheckAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJson.Options, ct);
    }

    public async Task<JsonElement?> ReadStageAsync(Guid runId, string stage, Guid measurementId, CancellationToken ct)
    {
        using var request = Create(HttpMethod.Get, options.Value.StorageUrl,
            $"v1/measurements/{runId}/artifacts/download/pipeline/{stage}.json", measurementId);
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await CheckAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJson.Options, ct);
    }

    public async Task<(JsonElement Data, string Uri)> SaveStageAsync(Guid runId, string stage, JsonElement data, Guid id, CancellationToken ct)
    {
        var path = $"v1/measurements/{runId}/artifacts/pipeline/{stage}.json";
        using var request = Create(HttpMethod.Put, options.Value.StorageUrl, path, id);
        request.Content = JsonContent.Create(data, options: PipelineJson.Options);
        request.Headers.Add("X-Provenance", "measurement-orchestrator:v1");
        using var response = await client.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            // The original response may have been lost after a successful immutable upload.
            var saved = await ReadStageAsync(runId, stage, id, ct)
                ?? throw new PipelineException("ArtifactConflict", $"Missing persisted result of {stage}.");
            return (saved, await StageUriAsync(runId, stage, id, ct));
        }
        await CheckAsync(response, ct);
        var metadata = await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJson.Options, ct);
        return (data, metadata.GetProperty("uri").GetString()!);
    }

    public async Task<string> StageUriAsync(Guid runId, string stage, Guid id, CancellationToken ct)
    {
        var metadata = await GetAsync(options.Value.StorageUrl,
            $"v1/measurements/{runId}/artifacts/metadata/pipeline/{stage}.json", id, ct);
        return metadata.GetProperty("uri").GetString()!;
    }

    public async Task<byte[]> DownloadAsync(Guid runId, string artifactUri, Guid id, CancellationToken ct)
    {
        // Resolve through verified StorageService reads, never arbitrary URLs from upstream responses.
        var uri = new Uri(artifactUri, UriKind.Absolute);
        var prefix = $"/measurements/{runId:D}/";
        if (uri.Scheme != "s3" || !uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            throw new PipelineException("InvalidArtifactReference", "Artifact does not belong to this processing attempt.");
        var relative = uri.AbsolutePath[prefix.Length..];
        if (relative.Contains("..", StringComparison.Ordinal)) throw new PipelineException("InvalidArtifactReference", "Invalid artifact path.");
        using var request = Create(HttpMethod.Get, options.Value.StorageUrl,
            $"v1/measurements/{runId}/artifacts/download/{relative}", id);
        using var response = await client.SendAsync(request, ct);
        await CheckAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private static HttpRequestMessage Create(HttpMethod method, string baseUrl, string path, Guid id)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root) ||
            root.Scheme is not ("http" or "https")) throw new PipelineException("NotConfigured", "Invalid pipeline service URL.");
        var request = new HttpRequestMessage(method, new Uri(root, path));
        request.Headers.Add("X-Measurement-ID", id.ToString());
        request.Headers.Add("X-Correlation-ID", id.ToString());
        return request;
    }

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct);
        var code = $"Http{(int)response.StatusCode}";
        var detail = text;
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.TryGetProperty("code", out var value)) code = value.GetString() ?? code;
            if (root.TryGetProperty("detail", out value) || root.TryGetProperty("error", out value) || root.TryGetProperty("title", out value))
                detail = value.GetString() ?? text;
        }
        catch (JsonException) { }
        throw new PipelineException(code, $"{response.RequestMessage?.RequestUri?.AbsolutePath}: {detail[..Math.Min(detail.Length, 1500)]}",
            code != "NotConfigured" && (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500));
    }
}
