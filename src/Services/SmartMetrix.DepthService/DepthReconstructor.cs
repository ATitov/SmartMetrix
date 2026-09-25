using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartMetrix.Contracts;
using SmartMetrix.Domain;

namespace SmartMetrix.DepthService;

public interface IDepthArtifactStore
{
    Task<Uri> PutAsync(Guid measurementId, string path, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

public sealed class HttpDepthArtifactStore(HttpClient client, IOptions<DepthOptions> configured) : IDepthArtifactStore
{
    private readonly DepthOptions _options = configured.Value;

    public async Task<Uri> PutAsync(Guid measurementId, string path, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"{_options.StorageBaseUrl.TrimEnd('/')}/v1/measurements/{measurementId:D}/artifacts/{path}");
        request.Content = new ByteArrayContent(content.ToArray());
        request.Content.Headers.ContentType = new(contentType);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var metadata = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!metadata.TryGetProperty("uri", out var uri) || !Uri.TryCreate(uri.GetString(), UriKind.Absolute, out var parsed))
            throw new InvalidOperationException("Storage service returned no artifact URI.");
        return parsed;
    }
}

public sealed class DepthReconstructor(IStereoBackend backend, IDepthArtifactStore artifacts, IOptions<DepthOptions> configured)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DepthOptions _options = configured.Value;

    public async Task<ReconstructionResult> ReconstructAsync(Guid measurementId, ReconstructionRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        if (request.Calibration.Rectification is not null)
        {
            var rectified = await RectifiedDepthProcessor.ComputeAsync(request, backend, _options, cancellationToken);
            var pixels = rectified.Points;
            var valid = pixels.Where(p => p.DepthConfidence > 0).Select(p => new Point3((float)p.XMetres, (float)p.YMetres, (float)p.ZMetres, (float)p.DepthConfidence)).ToList();
            var mask = pixels.Select(p => p.DepthConfidence > 0 ? (byte)Math.Clamp(Math.Round(p.DepthConfidence * 255), 1, 255) : (byte)0).ToArray();
            return await StoreAsync(measurementId, request, request.Calibration.Rectification.Width, request.Calibration.Rectification.Height,
                valid, pixels, mask, pixels.Length - valid.Count, rectified.SelectedBaselines, cancellationToken, rectified.Checksums);
        }
        var frames = request.Frames.ToDictionary(frame => frame.CameraId, StringComparer.OrdinalIgnoreCase);
        var maps = request.Calibration.Pairs.Select(pair => new PairResult(
            pair,
            backend.Compute(frames[pair.LeftCameraId], frames[pair.RightCameraId], pair))).ToArray();
        var width = maps[0].Map.Width;
        var height = maps[0].Map.Height;
        var points = new List<Point3>(width * height);
        var organized = new OrganizedDepthPoint[width * height];
        var confidence = new byte[width * height];
        var selected = maps.ToDictionary(map => PairName(map.Calibration), _ => 0, StringComparer.OrdinalIgnoreCase);
        var invalid = 0;

        for (var index = 0; index < width * height; index++)
        {
            Candidate? best = null;
            foreach (var pair in maps)
            {
                var sample = pair.Map.Samples[index];
                if (!sample.IsValid) continue;
                var depth = pair.Calibration.Fx * pair.Calibration.BaselineMetres / sample.Disparity;
                if (depth < _options.NearDistanceMetres || depth > _options.FarDistanceMetres) continue;
                var rangePreference = (float)Math.Clamp(pair.Calibration.BaselineMetres * pair.Calibration.Fx /
                    (depth * Math.Max(_options.MinimumDisparity, 1)), 0.25, 1.0);
                var candidate = new Candidate(pair.Calibration, sample, depth, sample.Confidence * rangePreference);
                if (best is null || candidate.Score > best.Value.Score) best = candidate;
            }

            if (best is null) { organized[index] = new(0, 0, 0, 0); invalid++; continue; }
            var chosen = best.Value;
            var x = index % width;
            var y = index / width;
            var tx = chosen.Calibration.Translation.ElementAtOrDefault(0);
            var ty = chosen.Calibration.Translation.ElementAtOrDefault(1);
            var tz = chosen.Calibration.Translation.ElementAtOrDefault(2);
            var cameraX = (x - chosen.Calibration.Cx) * chosen.Depth / chosen.Calibration.Fx;
            var cameraY = (y - chosen.Calibration.Cy) * chosen.Depth / chosen.Calibration.Fy;
            var rotation = chosen.Calibration.Rotation;
            var rigX = rotation.Length == 9 ? rotation[0] * cameraX + rotation[1] * cameraY + rotation[2] * chosen.Depth : cameraX;
            var rigY = rotation.Length == 9 ? rotation[3] * cameraX + rotation[4] * cameraY + rotation[5] * chosen.Depth : cameraY;
            var rigZ = rotation.Length == 9 ? rotation[6] * cameraX + rotation[7] * cameraY + rotation[8] * chosen.Depth : chosen.Depth;
            points.Add(new Point3((float)(rigX + tx), (float)(rigY + ty), (float)(rigZ + tz), chosen.Sample.Confidence));
            organized[index] = new(rigX + tx, rigY + ty, rigZ + tz, chosen.Sample.Confidence);
            confidence[index] = (byte)Math.Clamp(MathF.Round(chosen.Sample.Confidence * 255), 1, 255);
            selected[PairName(chosen.Calibration)]++;
        }

        return await StoreAsync(measurementId, request, width, height, points, organized, confidence, invalid, selected, cancellationToken);
    }

    private async Task<ReconstructionResult> StoreAsync(Guid measurementId, ReconstructionRequest request, int width, int height,
        List<Point3> points, OrganizedDepthPoint[] organized, byte[] confidence, int invalid, IReadOnlyDictionary<string, int> selected,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? checksums = null)
    {
        var cloudUri = await artifacts.PutAsync(measurementId, "depth/point-cloud.ply", "application/ply",
            Encoding.ASCII.GetBytes(ToPly(points)), cancellationToken);
        var confidenceUri = await artifacts.PutAsync(measurementId, "depth/confidence.pgm", "image/x-portable-graymap",
            ToPgm(width, height, confidence), cancellationToken);
        var created = new PointCloudCreated(new MeasurementId(measurementId), cloudUri, confidenceUri,
            request.Calibration.CameraRigCoordinateSystemId);
        var organizedUri = await artifacts.PutAsync(measurementId, "depth/organized-cloud.json", "application/json",
            JsonSerializer.SerializeToUtf8Bytes(new OrganizedDepthCloud(width, height, organized),
                JsonOptions), cancellationToken);
        return new ReconstructionResult(created, points.Count, invalid, selected, organizedUri, _options.Backend,
            checksums is null ? null : "CameraAOriginal", checksums);
    }

    private static void Validate(ReconstructionRequest request)
    {
        if (request.Calibration.SchemaVersion != 1) throw new ArgumentException("Unsupported calibration bundle schema.");
        if (request.Calibration.Pairs.Count == 0) throw new ArgumentException("Calibration has no stereo pairs.");
        var frames = request.Frames.ToDictionary(frame => frame.CameraId, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Calibration.Pairs)
        {
            if (!frames.ContainsKey(pair.LeftCameraId) || !frames.ContainsKey(pair.RightCameraId))
                throw new ArgumentException($"Frames for pair {PairName(pair)} are missing.");
            if (pair.BaselineMetres <= 0 || pair.Fx <= 0 || pair.Fy <= 0)
                throw new ArgumentException($"Calibration for pair {PairName(pair)} is invalid.");
        }
    }

    private static string PairName(StereoPairCalibration pair) => $"{pair.LeftCameraId}{pair.RightCameraId}";

    private static string ToPly(List<Point3> points)
    {
        var output = new StringBuilder()
            .AppendLine("ply").AppendLine("format ascii 1.0")
            .Append("element vertex ").AppendLine(points.Count.ToString(CultureInfo.InvariantCulture))
            .AppendLine("property float x").AppendLine("property float y").AppendLine("property float z")
            .AppendLine("property float confidence").AppendLine("end_header");
        foreach (var point in points)
            output.Append(point.X.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(point.Y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(point.Z.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .AppendLine(point.Confidence.ToString("R", CultureInfo.InvariantCulture));
        return output.ToString();
    }

    private static byte[] ToPgm(int width, int height, byte[] pixels)
    {
        var header = Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
        var result = new byte[header.Length + pixels.Length];
        header.CopyTo(result, 0); pixels.CopyTo(result, header.Length);
        return result;
    }

    private sealed record PairResult(StereoPairCalibration Calibration, DisparityMap Map);
    private readonly record struct Candidate(StereoPairCalibration Calibration, DisparitySample Sample, double Depth, float Score);
}
