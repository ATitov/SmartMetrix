using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartMetrix.Contracts;
using SmartMetrix.Domain;

namespace SmartMetrix.SegmentationService;

public interface ISegmentationArtifactStore
{
    Task<Uri> PutAsync(Guid measurementId, string path, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

public sealed class HttpSegmentationArtifactStore(HttpClient client, IOptions<SegmentationOptions> configured) : ISegmentationArtifactStore
{
    private readonly SegmentationOptions _options = configured.Value;
    public async Task<Uri> PutAsync(Guid measurementId, string path, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{_options.StorageBaseUrl.TrimEnd('/')}/v1/measurements/{measurementId:D}/artifacts/{path}");
        request.Content = new ByteArrayContent(content.ToArray()); request.Content.Headers.ContentType = new(contentType);
        using var response = await client.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
        var metadata = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        if (!metadata.TryGetProperty("uri", out var value) || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Storage service returned no artifact URI.");
        return uri;
    }
}

public sealed class SegmentationProcessor(ISegmentationBackend backend, ISegmentationArtifactStore artifacts, IOptions<SegmentationOptions> configured)
{
    private readonly SegmentationOptions _options = configured.Value;

    public async Task<SegmentationResult> ProcessAsync(Guid measurementId, SegmentationRequest request, CancellationToken cancellationToken)
    {
        Validate(request.Frame);
        await backend.LoadAndWarmupAsync(cancellationToken);
        var frame = request.Frame; var pixels = frame.Width * frame.Height;
        var scores = new float[pixels * 3]; var weights = new float[pixels];
        foreach (var origin in TileOrigins(frame.Width, frame.Height, backend.Descriptor.Width, backend.Descriptor.Height, _options.TileOverlap))
        {
            var tile = Extract(frame, origin.X, origin.Y, backend.Descriptor.Width, backend.Descriptor.Height);
            var prediction = backend.Predict(tile);
            for (var y = 0; y < Math.Min(tile.Height, frame.Height - origin.Y); y++)
                for (var x = 0; x < Math.Min(tile.Width, frame.Width - origin.X); x++)
                {
                    var source = y * tile.Width + x; var target = (origin.Y + y) * frame.Width + origin.X + x;
                    var weight = EdgeWeight(x, y, tile.Width, tile.Height, _options.TileOverlap);
                    scores[target * 3 + prediction.Classes[source]] += prediction.Confidence[source] * weight;
                    weights[target] += weight;
                }
        }
        var mask = new byte[pixels]; var confidence = new byte[pixels]; var counts = new int[3]; var confidenceSum = 0d;
        for (var i = 0; i < pixels; i++)
        {
            var best = 0; for (var c = 1; c < 3; c++) if (scores[i * 3 + c] > scores[i * 3 + best]) best = c;
            mask[i] = (byte)best; counts[best]++;
            var value = weights[i] <= 0 ? 0 : scores[i * 3 + best] / weights[i];
            confidence[i] = (byte)Math.Clamp(MathF.Round(value * 255), 0, 255); confidenceSum += value;
        }
        var average = pixels == 0 ? 0 : confidenceSum / pixels;
        var maskUri = await artifacts.PutAsync(measurementId, "segmentation/mask.pgm", "image/x-portable-graymap", ToPgm(frame.Width, frame.Height, mask, 2), cancellationToken);
        var confidenceUri = await artifacts.PutAsync(measurementId, "segmentation/confidence.pgm", "image/x-portable-graymap", ToPgm(frame.Width, frame.Height, confidence, 255), cancellationToken);
        return new(new SegmentationCreated(new MeasurementId(measurementId), maskUri, backend.Descriptor.Version, average), confidenceUri,
            average < _options.MinimumConfidence, Enum.GetValues<MaskClass>().ToDictionary(x => x.ToString(), x => counts[(int)x]));
    }

    private void Validate(SegmentationFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Channels != backend.Descriptor.Channels ||
            !frame.PixelFormat.Equals(backend.Descriptor.PixelFormat, StringComparison.OrdinalIgnoreCase) ||
            (long)frame.Width * frame.Height * frame.Channels != frame.Pixels.Length)
            throw new ArgumentException($"Frame must be a non-empty {backend.Descriptor.PixelFormat} image with {backend.Descriptor.Channels} channels.");
    }

    public static IReadOnlyList<(int X, int Y)> TileOrigins(int width, int height, int tileWidth, int tileHeight, int overlap)
    {
        static int[] Axis(int size, int tile, int step) { var values = new List<int>(); for (var p = 0; p < size; p += step) values.Add(Math.Min(p, Math.Max(0, size - tile))); return values.Distinct().ToArray(); }
        var xs = Axis(width, tileWidth, tileWidth - overlap); var ys = Axis(height, tileHeight, tileHeight - overlap);
        return ys.SelectMany(y => xs.Select(x => (x, y))).ToArray();
    }

    private static TileInput Extract(SegmentationFrame frame, int left, int top, int width, int height)
    {
        var result = new byte[width * height * 3];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(left + x, frame.Width - 1); var sourceY = Math.Min(top + y, frame.Height - 1);
                Buffer.BlockCopy(frame.Pixels, (sourceY * frame.Width + sourceX) * 3, result, (y * width + x) * 3, 3);
            }
        return new(width, height, result);
    }

    private static float EdgeWeight(int x, int y, int width, int height, int overlap)
    {
        if (overlap == 0) return 1;
        var edge = Math.Min(Math.Min(x + 1, width - x), Math.Min(y + 1, height - y));
        return Math.Clamp(edge / (float)(overlap + 1), 0.01f, 1f);
    }

    private static byte[] ToPgm(int width, int height, byte[] data, int max)
    {
        var header = Encoding.ASCII.GetBytes($"P5\n{width} {height}\n{max}\n"); var result = new byte[header.Length + data.Length];
        header.CopyTo(result, 0); data.CopyTo(result, header.Length); return result;
    }
}
