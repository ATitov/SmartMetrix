using System.Net.Http.Json;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SmartMetrix.Contracts;
using SmartMetrix.Domain;

namespace SmartMetrix.SegmentationService;

public sealed class StoneVisionClient(HttpClient client)
{
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        var health = await client.GetFromJsonAsync<JsonElement>("health", cancellationToken);
        return health.ValueKind == JsonValueKind.Object &&
            health.TryGetProperty("models_loaded", out var loaded) && loaded.ValueKind == JsonValueKind.True;
    }

    public async Task<JsonElement> DetectAsync(Guid measurementId, SegmentationFrame frame, DetectionParameters parameters, CancellationToken cancellationToken)
    {
        // P6 preserves the original RGB pixels and dimensions without an image codec dependency.
        using var content = new MultipartFormDataContent();
        var image = new ByteArrayContent(EncodeImage("P6", frame.Width, frame.Height, 255, frame.Pixels));
        image.Headers.ContentType = new("image/x-portable-pixmap");
        content.Add(image, "image", "frame.ppm");
        void Add(string name, IFormattable value) => content.Add(new StringContent(value.ToString(null, CultureInfo.InvariantCulture)), name);
        Add("yolo_conf", parameters.YoloConfidence);
        Add("yolo_iou", parameters.YoloIou);
        Add("yolo_imgsz", parameters.YoloImageSize);
        Add("yolo_max_det", parameters.YoloMaxDetections);
        content.Add(new StringContent(parameters.YoloTta ? "true" : "false"), "yolo_tta");
        Add("mask_min_area", parameters.MaskMinimumArea);
        Add("tile_size", parameters.TileSize);
        Add("tile_overlap", parameters.TileOverlap);
        using var request = new HttpRequestMessage(HttpMethod.Post, "detect_json") { Content = content };
        request.Headers.Add("X-Measurement-ID", measurementId.ToString("D"));
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }

    internal static byte[] EncodeImage(string format, int width, int height, int max, byte[] pixels)
    {
        var header = Encoding.ASCII.GetBytes(FormattableString.Invariant($"{format}\n{width} {height}\n{max}\n"));
        var result = new byte[checked(header.Length + pixels.Length)];
        header.CopyTo(result, 0);
        pixels.CopyTo(result, header.Length);
        return result;
    }
}

public sealed class StoneVisionHealthCheck(StoneVisionClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await client.IsReadyAsync(timeout.Token)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("StoneVision models are not loaded.");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            return HealthCheckResult.Unhealthy("StoneVision is unavailable.", error);
        }
    }
}

public sealed class StoneVisionProcessor(StoneVisionClient client, ISegmentationArtifactStore artifacts,
    IOptions<SegmentationOptions> configured) : ISegmentationProcessor
{
    public async Task<SegmentationResult> ProcessAsync(Guid measurementId, SegmentationRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        SegmentationValidation.Validate(request);
        var frame = request.Frame;
        var parameters = request.Detection ?? new DetectionParameters();
        var response = await client.DetectAsync(measurementId, frame, parameters, cancellationToken);
        var decoded = Decode(response, frame.Width, frame.Height, cancellationToken);
        var (mask, confidence, labels, detections, overlap) = decoded;
        var rawUri = await artifacts.PutAsync(measurementId, "segmentation/stonevision.json", "application/json",
            Encoding.UTF8.GetBytes(response.GetRawText()), cancellationToken);
        var instances = new List<StoneInstance>();
        foreach (var detection in detections)
        {
            var uri = await artifacts.PutAsync(measurementId, $"segmentation/instances/{detection.Id}.json", "application/json",
                Encoding.UTF8.GetBytes(detection.Rle.GetRawText()), cancellationToken);
            instances.Add(new(detection.Id, detection.Box, detection.Area, uri, detection.Score, detection.SamScore));
        }
        var instanceMapUri = await artifacts.PutAsync(measurementId, "segmentation/instances.json", "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                width = frame.Width,
                height = frame.Height,
                cameraId = frame.CameraId,
                pixelGrid = frame.PixelGrid,
                labels
            }), cancellationToken);
        var maskUri = await artifacts.PutAsync(measurementId, "segmentation/mask.pgm", "image/x-portable-graymap",
            StoneVisionClient.EncodeImage("P5", frame.Width, frame.Height, 2, mask), cancellationToken);
        var confidenceUri = await artifacts.PutAsync(measurementId, "segmentation/confidence.pgm", "image/x-portable-graymap",
            StoneVisionClient.EncodeImage("P5", frame.Width, frame.Height, 255, confidence), cancellationToken);
        var average = confidence.Average(value => value / 255d);
        var rocks = mask.Count(value => value == (byte)MaskClass.Rock);
        var warnings = new List<SegmentationWarning>();
        if (detections.Count == 0) warnings.Add(new("NoDetections", "No stones were detected; background confidence is unknown."));
        if (average < configured.Value.MinimumConfidence) warnings.Add(new("LowConfidence", "Mean confidence across the entire frame is below the configured threshold."));
        if (overlap) warnings.Add(new("OverlappingInstances", "Overlapping pixels belong to the highest YOLO confidence; ties use the lower instance ID. Original masks are preserved."));
        return new(new SegmentationCreated(new MeasurementId(measurementId), maskUri, configured.Value.ModelVersion, average),
            confidenceUri, average < configured.Value.MinimumConfidence,
            new Dictionary<string, int> { ["Background"] = mask.Length - rocks, ["Rock"] = rocks, ["Crack"] = 0 }, IsTestData: false)
        {
            Image = new(frame.Width, frame.Height, frame.PixelFormat, frame.CameraId, frame.PixelGrid),
            Provenance = new("StoneVision", configured.Value.ModelVersion, configured.Value.StoneVisionVersion, configured.Value.WeightsVersion),
            ProcessingMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Detection = parameters,
            Instances = instances,
            InstanceMapUri = instanceMapUri,
            RawResultUri = rawUri,
            Warnings = warnings
        };
    }

    private sealed record DecodedInstance(int Id, PixelBoundingBox Box, int Area, double Score, double? SamScore, JsonElement Rle);
    private sealed record DecodedResult(byte[] Mask, byte[] Confidence, int[] Labels, List<DecodedInstance> Instances, bool Overlap);

    private static DecodedResult Decode(JsonElement response, int width, int height, CancellationToken cancellationToken)
    {
        try
        {
            if (!response.GetProperty("success").GetBoolean()) throw new InvalidDataException("StoneVision detection failed.");
            var coco = response.GetProperty("coco_format");
            var images = coco.GetProperty("images");
            if (images.GetArrayLength() != 1 || images[0].GetProperty("width").GetInt32() != width ||
                images[0].GetProperty("height").GetInt32() != height)
                throw new InvalidDataException("StoneVision image dimensions do not match the input frame.");
            var imageId = images[0].GetProperty("id").GetInt32();
            var mask = new byte[width * height];
            // An undetected pixel has unknown confidence, not certain background confidence.
            var confidence = new byte[mask.Length];
            var labels = new int[mask.Length];
            var scores = new Dictionary<int, double>();
            var instances = new List<DecodedInstance>();
            var overlap = false;
            foreach (var annotation in coco.GetProperty("annotations").EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instanceId = annotation.GetProperty("id").GetInt32();
                if (instanceId <= 0 || scores.ContainsKey(instanceId)) throw new InvalidDataException("StoneVision instance IDs must be unique positive integers.");
                if (annotation.GetProperty("image_id").GetInt32() != imageId || annotation.GetProperty("category_id").GetInt32() != 1)
                    throw new InvalidDataException("Unexpected StoneVision annotation image or category.");
                if (!annotation.TryGetProperty("segmentation", out var rle) || rle.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("StoneVision returned a detection without a SAM mask; bounding boxes cannot be used as measurement masks.");
                var size = rle.GetProperty("size");
                if (size.GetArrayLength() != 2 || size[0].GetInt32() != height || size[1].GetInt32() != width)
                    throw new InvalidDataException("StoneVision mask dimensions do not match the input frame.");
                var score = annotation.GetProperty("yolo_confidence").GetDouble();
                if (!double.IsFinite(score) || score < 0 || score > 1)
                    throw new InvalidDataException("Invalid StoneVision confidence.");
                double? samScore = annotation.TryGetProperty("sam_score", out var sam) && sam.ValueKind != JsonValueKind.Null ? sam.GetDouble() : null;
                if (samScore is { } quality && (!double.IsFinite(quality) || quality is < 0 or > 1))
                    throw new InvalidDataException("Invalid StoneVision SAM score.");
                scores.Add(instanceId, score);
                var area = 0; var minX = width; var minY = height; var maxX = -1; var maxY = -1;
                var value = (byte)Math.Round(score * 255);
                var offset = 0;
                var foreground = false;
                foreach (var run in rle.GetProperty("counts").EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = run.GetInt32();
                    if (length < 0 || length > mask.Length - offset)
                        throw new InvalidDataException("Invalid StoneVision RLE length.");
                    if (foreground)
                        for (var i = offset; i < offset + length; i++)
                        {
                            var index = (i % height) * width + i / height;
                            var x = i / height; var y = i % height;
                            area++; minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                            var existing = labels[index];
                            if (existing != 0) overlap = true;
                            if (existing == 0 || score > scores[existing] || score == scores[existing] && instanceId < existing)
                                labels[index] = instanceId;
                            mask[index] = (byte)MaskClass.Rock;
                            confidence[index] = Math.Max(confidence[index], value);
                        }
                    offset += length;
                    foreground = !foreground;
                }
                if (offset != mask.Length) throw new InvalidDataException("Incomplete StoneVision RLE mask.");
                if (area == 0) throw new InvalidDataException("StoneVision returned an empty instance mask.");
                instances.Add(new(instanceId, new(minX, minY, maxX - minX + 1, maxY - minY + 1), area, score, samScore, rle.Clone()));
            }
            return new(mask, confidence, labels, instances, overlap);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid StoneVision COCO response.", error);
        }
    }
}
