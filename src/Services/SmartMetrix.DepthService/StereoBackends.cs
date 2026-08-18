using Microsoft.Extensions.Options;

namespace SmartMetrix.DepthService;

public interface IStereoBackend
{
    DisparityMap Compute(GrayFrame left, GrayFrame right, StereoPairCalibration calibration);
}

public sealed class NativeStereoBackend : IStereoBackend
{
    public DisparityMap Compute(GrayFrame left, GrayFrame right, StereoPairCalibration calibration) =>
        throw new StereoBackendNotConfiguredException(
            "libsmartmetrix_stereo (OpenCV CUDA/VPI) is not configured. Set Depth:Backend=Cpu or deploy the native library.");
}

public sealed class CpuStereoBackend(IOptions<DepthOptions> configured) : IStereoBackend
{
    private readonly DepthOptions _options = configured.Value;

    public DisparityMap Compute(GrayFrame left, GrayFrame right, StereoPairCalibration calibration)
    {
        Validate(left, right);
        var forward = Match(left, right, -1);
        var reverse = Match(right, left, 1);
        ApplyConsistency(forward, reverse, left.Width, left.Height);
        RemoveSpeckles(forward, left.Width);
        return new DisparityMap(left.Width, left.Height, forward);
    }

    private DisparitySample[] Match(GrayFrame left, GrayFrame right, int direction)
    {
        var output = Enumerable.Repeat(DisparitySample.Invalid, left.Pixels.Length).ToArray();
        var radius = _options.MatchRadius;
        for (var y = radius; y < left.Height - radius; y++)
            for (var x = radius; x < left.Width - radius; x++)
            {
                var best = int.MaxValue;
                var second = int.MaxValue;
                var bestDisparity = 0;
                var available = direction < 0 ? x - radius : left.Width - radius - 1 - x;
                var max = Math.Min(_options.MaximumDisparity, available);
                for (var disparity = _options.MinimumDisparity; disparity <= max; disparity++)
                {
                    var cost = 0;
                    for (var dy = -radius; dy <= radius; dy++)
                        for (var dx = -radius; dx <= radius; dx++)
                            cost += Math.Abs(left.Pixels[(y + dy) * left.Width + x + dx] -
                                             right.Pixels[(y + dy) * right.Width + x + dx + direction * disparity]);
                    if (cost < best) { second = best; best = cost; bestDisparity = disparity; }
                    else if (cost < second) second = cost;
                }

                if (bestDisparity == 0) continue;
                var uniqueness = second == int.MaxValue ? 0f : Math.Clamp((second - best) / (float)Math.Max(second, 1), 0, 1);
                var photometric = 1f - Math.Clamp(best / (float)((radius * 2 + 1) * (radius * 2 + 1) * 255), 0, 1);
                var confidence = uniqueness * photometric;
                if (confidence >= _options.MinimumConfidence)
                    output[y * left.Width + x] = new DisparitySample(bestDisparity, confidence);
            }
        return output;
    }

    private void RemoveSpeckles(DisparitySample[] samples, int width)
    {
        var visited = new bool[samples.Length];
        var component = new List<int>();
        var queue = new Queue<int>();
        for (var start = 0; start < samples.Length; start++)
        {
            if (visited[start] || !samples[start].IsValid) continue;
            component.Clear(); queue.Enqueue(start); visited[start] = true;
            while (queue.TryDequeue(out var index))
            {
                component.Add(index);
                var x = index % width;
                int[] neighbours = [index - 1, index + 1, index - width, index + width];
                foreach (var next in neighbours)
                {
                    if (next < 0 || next >= samples.Length || visited[next] || !samples[next].IsValid) continue;
                    if (Math.Abs(next % width - x) > 1 || Math.Abs(samples[next].Disparity - samples[index].Disparity) > 1) continue;
                    visited[next] = true; queue.Enqueue(next);
                }
            }
            if (component.Count < _options.MinimumSpeckleSize)
                foreach (var index in component) samples[index] = DisparitySample.Invalid;
        }
    }

    private static void Validate(GrayFrame left, GrayFrame right)
    {
        if (left.Width != right.Width || left.Height != right.Height ||
            left.Pixels.Length != left.Width * left.Height || right.Pixels.Length != right.Width * right.Height)
            throw new ArgumentException("Stereo frames must have equal dimensions and valid 8-bit pixel buffers.");
    }

    private void ApplyConsistency(DisparitySample[] forward, DisparitySample[] reverse, int width, int height)
    {
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var sample = forward[index];
                if (!sample.IsValid) continue;
                var rightX = x - (int)MathF.Round(sample.Disparity);
                if (rightX < 0 || !reverse[y * width + rightX].IsValid ||
                    Math.Abs(reverse[y * width + rightX].Disparity - sample.Disparity) > _options.LeftRightTolerancePixels)
                    forward[index] = DisparitySample.Invalid;
            }
    }
}
