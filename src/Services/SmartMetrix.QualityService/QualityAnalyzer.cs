using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json;

namespace SmartMetrix.QualityService;

public interface ILensContaminationModel
{
    ValueTask<double> PredictAsync(QualityFrame frame, CancellationToken cancellationToken = default);
}

// Deterministic fallback. An ONNX implementation can replace this registration without changing the analyzer.
public sealed class HeuristicLensContaminationModel(IOptionsMonitor<QualityOptions> configured) : ILensContaminationModel
{
    public ValueTask<double> PredictAsync(QualityFrame frame, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Predict(frame, configured.CurrentValue.Metrics));

    public static double Predict(QualityFrame frame, QualityMetricProfile profile)
    {
        if (frame.GrayscalePixels.Length == 0) return 1;
        var mean = frame.GrayscalePixels.Average(x => (double)x);
        var variance = frame.GrayscalePixels.Average(x => Math.Pow(x - mean, 2));
        return Clamp01(1 - Math.Sqrt(variance) / profile.ContaminationStandardDeviationScale);
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
}

public interface IQualityResultStore
{
    void Save(QualityResult result);
    bool TryGet(Guid measurementId, out QualityResult? result);
}

public sealed class InMemoryQualityResultStore : IQualityResultStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, QualityResult> _results = new();
    public void Save(QualityResult result) => _results[result.MeasurementId] = result;
    public bool TryGet(Guid measurementId, out QualityResult? result) => _results.TryGetValue(measurementId, out result);
}

public sealed class QualityAnalyzer(
    IOptionsMonitor<QualityOptions> options,
    ILensContaminationModel contaminationModel,
    IQualityResultStore store)
{
    private static readonly string[] RequiredCameras = ["A", "B", "C"];

    public async Task<QualityResult> AssessAsync(Guid measurementId, QualityRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Frames is null || request.Frames.Any(x => x is null || x.GrayscalePixels is null) || string.IsNullOrWhiteSpace(request.SceneType))
            throw new ArgumentException("Frames, pixel arrays and scene type are required.");
        var inputHash = Fingerprint(request);
        if (store.TryGet(measurementId, out var existing))
        {
            if (existing!.InputSha256 != inputHash) throw new InvalidOperationException("A different frame set already exists for this processing run.");
            return existing;
        }
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var configured = options.CurrentValue;
        if (!configured.Scenes.TryGetValue(request.SceneType, out var thresholds))
        {
            reasons.Add(QualityReasonCodes.UnknownSceneType);
            configured.Scenes.TryGetValue(configured.DefaultSceneType, out thresholds);
        }
        thresholds ??= new QualityThresholds();

        var byCamera = request.Frames
            .Where(x => !string.IsNullOrWhiteSpace(x.CameraId))
            .GroupBy(x => x.CameraId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        if (RequiredCameras.Any(camera => !byCamera.ContainsKey(camera)))
            reasons.Add(QualityReasonCodes.MissingFrame);
        if (byCamera.Keys.Any(camera => !RequiredCameras.Contains(camera, StringComparer.OrdinalIgnoreCase)) ||
            byCamera.Values.Any(frames => frames.Length != 1))
            reasons.Add(QualityReasonCodes.InconsistentFrameSet);

        var validFrames = request.Frames.Where(IsValid).ToArray();
        if (validFrames.Length != request.Frames.Count) reasons.Add(QualityReasonCodes.InvalidFrame);
        if (validFrames.Length > 1 && validFrames.Any(x => x.Width != validFrames[0].Width || x.Height != validFrames[0].Height))
            reasons.Add(QualityReasonCodes.InconsistentFrameSet);
        if (validFrames.Length > 1 && validFrames.Max(x => x.HardwareTimestampNanoseconds) - validFrames.Min(x => x.HardwareTimestampNanoseconds) > thresholds.MaximumTimestampSkewNanoseconds)
            reasons.Add(QualityReasonCodes.FramesNotSynchronized);

        var metrics = new List<FrameQualityMetrics>(validFrames.Length);
        foreach (var frame in validFrames.OrderBy(x => x.CameraId, StringComparer.Ordinal))
        {
            var frameMetrics = await CalculateAsync(frame, configured.Metrics, cancellationToken);
            metrics.Add(frameMetrics);
            if (frameMetrics.Sharpness < thresholds.MinimumSharpness) reasons.Add(QualityReasonCodes.LowSharpness);
            if (frameMetrics.Exposure < thresholds.MinimumExposure) reasons.Add(QualityReasonCodes.BadExposure);
            if (frameMetrics.Saturation > thresholds.MaximumSaturation) reasons.Add(QualityReasonCodes.HighSaturation);
            if (frameMetrics.Texture < thresholds.MinimumTexture) reasons.Add(QualityReasonCodes.LowTexture);
            if (frameMetrics.ShadowFraction > thresholds.MaximumShadowFraction) reasons.Add(QualityReasonCodes.ExcessiveShadow);
            if (frameMetrics.LensContamination > thresholds.MaximumLensContamination) reasons.Add(QualityReasonCodes.LensContamination);
        }

        var result = new QualityResult(measurementId, reasons.Count == 0, request.SceneType, thresholds.Version,
            metrics, reasons.Order(StringComparer.Ordinal).ToArray(), DateTimeOffset.UtcNow)
        {
            ThresholdProfile = thresholds,
            MetricProfile = configured.Metrics,
            ContaminationMethod = contaminationModel.GetType().Name,
            InputSha256 = inputHash
        };
        store.Save(result);
        return result;
    }

    private async Task<FrameQualityMetrics> CalculateAsync(QualityFrame frame, QualityMetricProfile profile, CancellationToken cancellationToken)
    {
        var pixels = frame.GrayscalePixels;
        var mean = pixels.Average(x => x / 255d);
        var variance = pixels.Average(x => Math.Pow(x / 255d - mean, 2));
        var laplacianSum = 0d;
        var count = 0;
        for (var y = 1; y < frame.Height - 1; y++)
            for (var x = 1; x < frame.Width - 1; x++)
            {
                var i = y * frame.Width + x;
                var laplacian = 4 * pixels[i] - pixels[i - 1] - pixels[i + 1] - pixels[i - frame.Width] - pixels[i + frame.Width];
                laplacianSum += laplacian * laplacian;
                count++;
            }
        var sharpness = count == 0 ? 0 : Clamp01((laplacianSum / count) / profile.SharpnessNormalization);
        var exposure = Clamp01(1 - Math.Abs(mean - 0.5) * 2);
        var saturation = pixels.Count(x => x <= profile.SaturationLow || x >= profile.SaturationHigh) / (double)pixels.Length;
        var texture = Clamp01(Math.Sqrt(variance) * profile.TextureScale);
        var shadow = pixels.Count(x => x < profile.ShadowThreshold) / (double)pixels.Length;
        var contamination = Clamp01(contaminationModel is HeuristicLensContaminationModel
            ? HeuristicLensContaminationModel.Predict(frame, profile)
            : await contaminationModel.PredictAsync(frame, cancellationToken));
        return new(frame.CameraId, sharpness, exposure, saturation, texture, shadow, contamination);
    }

    private static bool IsValid(QualityFrame frame) => frame.Width > 0 && frame.Height > 0 &&
        (long)frame.Width * frame.Height == frame.GrayscalePixels.Length;
    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private static string Fingerprint(QualityRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { request.SceneType, FrameCount = request.Frames.Count }));
        foreach (var frame in request.Frames.OrderBy(x => x.CameraId, StringComparer.Ordinal))
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { frame.CameraId, frame.Width, frame.Height, frame.HardwareTimestampNanoseconds, PixelCount = frame.GrayscalePixels.Length }));
            hash.AppendData(frame.GrayscalePixels);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
