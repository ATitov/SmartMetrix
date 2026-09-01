using Microsoft.Extensions.Options;
using SmartMetrix.DepthService;

namespace SmartMetrix.ArchitectureTests;

public sealed class DepthReconstructionTests
{
    [Fact]
    public async Task CpuFallbackReconstructsSyntheticMultiBaselineSceneWithinDepthBudget()
    {
        const int width = 128, height = 64, shiftAb = 6, shiftBc = 6;
        var random = new Random(1701);
        var source = new byte[width * height];
        random.NextBytes(source);
        var frames = new[]
        {
            Frame("A", source, 0), Frame("B", source, shiftAb), Frame("C", source, shiftAb + shiftBc)
        };
        var options = Options.Create(new DepthOptions
        {
            MaximumDisparity = 20,
            MatchRadius = 2,
            MinimumSpeckleSize = 4,
            MinimumConfidence = 0.05f,
            NearDistanceMetres = 2,
            FarDistanceMetres = 20
        });
        var store = new MemoryArtifactStore();
        var reconstructor = new DepthReconstructor(new CpuStereoBackend(options), store, options);
        var calibration = new DepthCalibrationBundle(1, "synthetic-v1", "rig:test",
        [
            Pair("A", "B", 0.7), Pair("B", "C", 0.8), Pair("A", "C", 1.5)
        ]);

        var result = await reconstructor.ReconstructAsync(Guid.NewGuid(), new ReconstructionRequest(frames, calibration), CancellationToken.None);

        Assert.True(result.ValidPointCount > 2_000);
        Assert.True(result.InvalidPixelCount > 0); // Borders/occlusions have an explicit invalid marker in the confidence map.
        Assert.Equal(2, store.Artifacts.Count);
        Assert.All(store.Artifacts.Keys, uri => Assert.True(uri.IsAbsoluteUri));
        var ply = System.Text.Encoding.ASCII.GetString(store.Artifacts.Single(x => x.Key.AbsolutePath.EndsWith(".ply")).Value);
        var depths = ply.Split('\n').SkipWhile(line => line.Trim() != "end_header").Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => double.Parse(line.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(Math.Abs(depths.Average() - 10.0), 0, 0.75);
    }

    [Fact]
    public void NativeBackendFailsExplicitlyWhenLibraryIsNotConfigured()
    {
        var frame = new GrayFrame("A", 1, 1, [0]);
        Assert.Throws<StereoBackendNotConfiguredException>(() =>
            new NativeStereoBackend(Options.Create(new DepthOptions())).Compute(frame, frame, Pair("A", "B", 0.7)));
    }

    private static GrayFrame Frame(string id, byte[] source, int shift)
    {
        const int width = 128, height = 64;
        var pixels = new byte[source.Length];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = x + shift < width ? source[y * width + x + shift] : (byte)0;
        return new GrayFrame(id, width, height, pixels);
    }

    private static StereoPairCalibration Pair(string left, string right, double baseline) =>
        new(left, right, baseline, 80, 80, 64, 32, [1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0], "s3://calibration/identity");

    private sealed class MemoryArtifactStore : IDepthArtifactStore
    {
        public Dictionary<Uri, byte[]> Artifacts { get; } = [];
        public Task<Uri> PutAsync(Guid measurementId, string path, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            var uri = new Uri($"s3://test/measurements/{measurementId:D}/{path}");
            Artifacts[uri] = content.ToArray();
            return Task.FromResult(uri);
        }
    }
}
