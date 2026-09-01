using Microsoft.Extensions.Options;
using System.Runtime.InteropServices;

namespace SmartMetrix.DepthService;

public interface IStereoBackend
{
    DisparityMap Compute(GrayFrame left, GrayFrame right, StereoPairCalibration calibration);
}

public sealed class NativeStereoBackend(IOptions<DepthOptions> configured) : IStereoBackend
{
    private const string LibraryName = "smartmetrix_stereo";
    private readonly DepthOptions options = configured.Value;

    public DisparityMap Compute(GrayFrame left, GrayFrame right, StereoPairCalibration calibration)
    {
        if (left.Width != right.Width || left.Height != right.Height ||
            left.Pixels.Length != left.Width * left.Height || right.Pixels.Length != right.Width * right.Height)
            throw new ArgumentException("Stereo frames must have equal dimensions and valid 8-bit pixel buffers.");

        var configuration = new NativeConfiguration
        {
            AbiVersion = 1,
            MinimumDisparity = options.MinimumDisparity,
            MaximumDisparity = options.MaximumDisparity,
            LeftRightTolerancePixels = options.LeftRightTolerancePixels,
            MinimumSpeckleSize = options.MinimumSpeckleSize,
            MinimumConfidence = options.MinimumConfidence,
            UniquenessRatio = options.UniquenessRatio,
            Provider = options.NativeProvider
        };
        try
        {
            var status = Native.Compute(ref configuration, left.Pixels, right.Pixels, left.Width, left.Height, out var output);
            if (status != NativeStatus.Ok) throw MapStatus(status);
            try
            {
                if (output.Count != left.Width * left.Height || output.Disparity == 0 || output.Confidence == 0)
                    throw new InvalidOperationException("Native stereo backend returned an invalid output buffer.");
                var disparities = new float[output.Count];
                var confidence = new float[output.Count];
                Marshal.Copy(output.Disparity, disparities, 0, output.Count);
                Marshal.Copy(output.Confidence, confidence, 0, output.Count);
                var samples = new DisparitySample[output.Count];
                for (var index = 0; index < samples.Length; index++)
                    samples[index] = float.IsFinite(disparities[index]) && disparities[index] > 0 && confidence[index] > 0
                        ? new(disparities[index], Math.Clamp(confidence[index], 0, 1))
                        : DisparitySample.Invalid;
                return new DisparityMap(left.Width, left.Height, samples);
            }
            finally { Native.Release(ref output); }
        }
        catch (DllNotFoundException) { throw NotConfigured(); }
        catch (EntryPointNotFoundException) { throw NotConfigured(); }
        catch (BadImageFormatException) { throw NotConfigured(); }
    }

    private static StereoBackendNotConfiguredException NotConfigured() => new(
        "libsmartmetrix_stereo is not installed or has an incompatible ABI. Deploy the OpenCV CUDA build or select Depth:Backend=Cpu.");
    private static Exception MapStatus(NativeStatus status) => status switch
    {
        NativeStatus.NotConfigured => NotConfigured(),
        NativeStatus.InvalidInput => new ArgumentException("Native stereo backend rejected the frame dimensions or configuration."),
        NativeStatus.CudaFailure => new InvalidOperationException("OpenCV CUDA stereo processing failed. Check CUDA runtime and GPU memory."),
        _ => new InvalidOperationException($"Native stereo backend failed with status {(int)status}.")
    };

    private enum NativeStatus { Ok = 0, NotConfigured = 1, InvalidInput = 2, CudaFailure = 3, Failure = 255 }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeConfiguration
    {
        public int AbiVersion, MinimumDisparity, MaximumDisparity;
        public float LeftRightTolerancePixels;
        public int MinimumSpeckleSize;
        public float MinimumConfidence;
        public int UniquenessRatio;
        [MarshalAs(UnmanagedType.LPUTF8Str)] public string Provider;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeOutput { public nint Disparity, Confidence; public int Count; }
    private static class Native
    {
        [DllImport(LibraryName, EntryPoint = "smartmetrix_stereo_compute")]
        internal static extern NativeStatus Compute(ref NativeConfiguration configuration, byte[] left, byte[] right,
            int width, int height, out NativeOutput output);
        [DllImport(LibraryName, EntryPoint = "smartmetrix_stereo_release")]
        internal static extern void Release(ref NativeOutput output);
    }
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
