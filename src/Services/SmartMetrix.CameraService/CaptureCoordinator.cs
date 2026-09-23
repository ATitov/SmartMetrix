using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace SmartMetrix.CameraService;

public sealed class CaptureCoordinator(ICameraAdapter adapter, CameraStorageClient storage, IOptions<CameraOptions> configured)
{
    private static readonly string[] RequiredCameraIds = ["A", "B", "C"];
    private readonly CameraOptions options = configured.Value;

    public async Task<CaptureResponse> CaptureAsync(Guid measurementId, CaptureRequest request, CancellationToken cancellationToken)
    {
        var exposedAt = DateTimeOffset.UtcNow;
        var frames = await adapter.CaptureAsync(cancellationToken);
        var ids = frames.Select(frame => frame.CameraId).Order(StringComparer.Ordinal).ToArray();
        if (frames.Count != 3 || !ids.SequenceEqual(RequiredCameraIds, StringComparer.Ordinal))
            throw new CameraCaptureException("FrameSetIncomplete", "Capture must contain exactly one frame from cameras A, B and C.");
        if (frames.Any(frame => frame.Payload.IsEmpty))
            throw new CameraCaptureException("FrameSetIncomplete", "A captured frame has an empty payload.");

        var skew = frames.Max(frame => frame.HardwareTimestampNanoseconds) - frames.Min(frame => frame.HardwareTimestampNanoseconds);
        if (skew > options.MaximumTimestampSkewNanoseconds)
            throw new CameraCaptureException("TimestampSkewExceeded", $"Frame timestamp skew {skew} ns exceeds {options.MaximumTimestampSkewNanoseconds} ns.");

        var stored = new List<StoredFrame>(3);
        foreach (var frame in frames.OrderBy(frame => frame.CameraId, StringComparer.Ordinal))
            stored.Add(await storage.StoreAsync(measurementId, frame, cancellationToken));

        return new CaptureResponse(measurementId, stored, skew, adapter.Name, request.CalibrationId, exposedAt, options.PixelFormat);
    }
}

public sealed class CameraStorageClient(HttpClient client)
{
    public async Task<StoredFrame> StoreAsync(Guid measurementId, CapturedFrame frame, CancellationToken cancellationToken)
    {
        var path = $"frames/{frame.CameraId.ToLowerInvariant()}/{frame.FrameId}.raw";
        var hash = Convert.ToHexStringLower(SHA256.HashData(frame.Payload.Span));
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/v1/measurements/{measurementId:D}/artifacts/{path}");
        request.Content = new ReadOnlyMemoryContent(frame.Payload);
        request.Content.Headers.ContentType = new(frame.ContentType);
        request.Headers.Add("X-Content-SHA256", hash);
        request.Headers.Add("X-Provenance", "camera-service:original");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new CameraCaptureException("StorageFailure", $"StorageService rejected camera {frame.CameraId}: {(int)response.StatusCode}.", 502);
        var metadata = await response.Content.ReadFromJsonAsync<StorageMetadata>(cancellationToken) ??
            throw new CameraCaptureException("StorageFailure", "StorageService returned an empty response.", 502);
        return new StoredFrame(frame.CameraId, frame.FrameId, frame.HardwareTimestampNanoseconds, metadata.Uri, metadata.Sha256);
    }
    private sealed record StorageMetadata(string Uri, string Sha256);
}

public static class CameraServices
{
    public static IServiceCollection AddCameraCapture(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CameraOptions>().Bind(configuration.GetSection(CameraOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        var adapterName = configuration[$"{CameraOptions.SectionName}:Adapter"] ?? "Arena";
        if (adapterName.Equals("Simulator", StringComparison.OrdinalIgnoreCase)) services.AddSingleton<ICameraAdapter, SimulatorCameraAdapter>();
        else if (adapterName.Equals("Arena", StringComparison.OrdinalIgnoreCase)) services.AddSingleton<ICameraAdapter, ArenaCameraAdapter>();
        else throw new InvalidOperationException($"Unknown camera adapter '{adapterName}'. Expected Arena or Simulator.");
        services.AddSingleton<CaptureCoordinator>();
        services.AddHttpClient<CameraStorageClient>((provider, client) =>
        {
            client.BaseAddress = new Uri(provider.GetRequiredService<IOptions<CameraOptions>>().Value.StorageServiceUrl);
        });
        return services;
    }
}
