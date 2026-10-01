using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace SmartMetrix.SegmentationService;

public sealed record CrackInferenceProvenance(string ModelVersion, string WeightsSha256, Uri RawResultUri);
public sealed record CrackPrediction(int SchemaVersion, int Width, int Height, string? CameraId, string? PixelGrid,
    byte[] Mask, byte[] Confidence, string ModelVersion, string WeightsSha256);

public sealed class CrackInferenceClient(HttpClient http, IOptions<SegmentationOptions> configured)
{
    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        var response = await http.GetFromJsonAsync<JsonElement>("health", ct);
        return response.ValueKind == JsonValueKind.Object && response.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True &&
            response.TryGetProperty("modelVersion", out var version) && version.GetString() == configured.Value.CrackModelVersion &&
            response.TryGetProperty("weightsSha256", out var weights) && weights.GetString() == configured.Value.CrackWeightsSha256;
    }

    public async Task<CrackPrediction> PredictAsync(Guid runId, SegmentationFrame frame, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/cracks") { Content = JsonContent.Create(new { processingRunId = runId, frame }) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(128 * 1024 * 1024, ct);
        var prediction = await response.Content.ReadFromJsonAsync<CrackPrediction>(ct)
            ?? throw new InvalidDataException("Crack inference returned an empty result.");
        Validate(prediction, frame, configured.Value);
        return prediction;
    }

    public static void Validate(CrackPrediction result, SegmentationFrame frame, SegmentationOptions options)
    {
        if (result.SchemaVersion != 1 || result.Width != frame.Width || result.Height != frame.Height ||
            result.CameraId != frame.CameraId || result.PixelGrid != frame.PixelGrid ||
            result.Mask is null || result.Confidence is null || result.Mask.Length != checked(frame.Width * frame.Height) ||
            result.Confidence.Length != result.Mask.Length || result.Mask.Any(x => x > 1) ||
            result.ModelVersion != options.CrackModelVersion || result.WeightsSha256 != options.CrackWeightsSha256)
            throw new InvalidDataException("Crack mask grid, encoding or model provenance does not match the configured inference contract.");
    }
}

public sealed class CrackInferenceHealthCheck(CrackInferenceClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { return await client.IsReadyAsync(timeout.Token) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Crack model or weights are not ready."); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        { return HealthCheckResult.Unhealthy("Crack inference is unavailable or returned invalid readiness."); }
    }
}
