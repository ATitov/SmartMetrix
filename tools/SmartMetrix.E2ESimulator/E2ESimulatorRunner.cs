using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartMetrix.BlockAnalysisService;
using SmartMetrix.Domain;
using SmartMetrix.GeoreferenceService;
using SmartMetrix.QualityService;
using SmartMetrix.SegmentationService;

namespace SmartMetrix.E2ESimulator;

public sealed class E2ESimulatorRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<E2EReport> RunAsync(string datasetPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        var dataset = JsonSerializer.Deserialize<GoldenDataset>(await File.ReadAllTextAsync(datasetPath, cancellationToken), JsonOptions)
            ?? throw new InvalidOperationException("Golden dataset is empty.");
        Validate(dataset);
        Directory.CreateDirectory(outputDirectory);
        var measurementId = DeterministicGuid(dataset.DatasetVersion);
        var total = Stopwatch.StartNew();
        var stages = new List<StageDiagnostic>();

        var captureTimer = Stopwatch.StartNew();
        var captured = SimulateCapture(dataset);
        captureTimer.Stop();
        stages.Add(new("capture", dataset.DatasetVersion, captureTimer.Elapsed.TotalMilliseconds,
            new Dictionary<string, double> { ["frames"] = captured.Gray.Count, ["timestampSkewNs"] = 2_000_000 }, []));

        var qualityTimer = Stopwatch.StartNew();
        var quality = await AssessQuality(measurementId, captured.Gray, dataset, cancellationToken);
        qualityTimer.Stop();
        stages.Add(new("quality", quality.ThresholdVersion, qualityTimer.Elapsed.TotalMilliseconds,
            new Dictionary<string, double> { ["accepted"] = quality.Accepted ? 1 : 0, ["reasons"] = quality.ReasonCodes.Count }, []));

        var depthTimer = Stopwatch.StartNew();
        var points = SimulateDepth(dataset);
        var pointCloudPath = Path.Combine(outputDirectory, "point-cloud.json");
        await WriteJson(pointCloudPath, points, cancellationToken);
        depthTimer.Stop();
        stages.Add(new("depth", dataset.CalibrationVersion, depthTimer.Elapsed.TotalMilliseconds,
            new Dictionary<string, double> { ["validPoints"] = points.Length, ["distanceMetres"] = dataset.DistanceMetres }, [pointCloudPath]));

        var segmentationTimer = Stopwatch.StartNew();
        var segmentation = await Segment(measurementId, captured.Rgb, dataset, outputDirectory, cancellationToken);
        segmentationTimer.Stop();
        stages.Add(new("segmentation", dataset.ModelVersion, segmentationTimer.Elapsed.TotalMilliseconds,
            new Dictionary<string, double> { ["rockPixels"] = segmentation.Mask.Count(x => x == 1), ["meanConfidence"] = segmentation.Confidence.Average() }, segmentation.Artifacts));

        var analysisTimer = Stopwatch.StartNew();
        var analysis = Analyze(measurementId, dataset, points, segmentation);
        var d50 = analysis.D50Millimetres ?? throw new InvalidOperationException("Synthetic acceptance fixture has no volume distribution.");
        var d80 = analysis.D80Millimetres ?? throw new InvalidOperationException("Synthetic acceptance fixture has no volume distribution.");
        analysisTimer.Stop();
        stages.Add(new("analysis", analysis.Provenance.Version, analysisTimer.Elapsed.TotalMilliseconds,
            new Dictionary<string, double> { ["d50Mm"] = d50, ["d80Mm"] = d80, ["blocks"] = analysis.Blocks.Count }, []));

        var geoTimer = Stopwatch.StartNew();
        var georeferenced = Georeference(measurementId, dataset, analysis);
        geoTimer.Stop();
        var centre = georeferenced.Blocks.Single().Centre;
        var coordinateError = Distance(centre, dataset.ExpectedPosition);
        stages.Add(new("georeference", dataset.TransformVersion, geoTimer.Elapsed.TotalMilliseconds,
            new Dictionary<string, double> { ["coordinateErrorMetres"] = coordinateError, ["blocks"] = georeferenced.Blocks.Count }, []));

        total.Stop();
        var d50Error = RelativeError(d50, dataset.ExpectedD50Millimetres);
        var d80Error = RelativeError(d80, dataset.ExpectedD80Millimetres);
        var rejected = quality.Accepted ? 0d : 1d;
        var checks = new Dictionary<string, bool>
        {
            ["qualityAccepted"] = quality.Accepted,
            ["d50Accuracy"] = d50Error <= dataset.Acceptance.MaximumD50RelativeError,
            ["d80Accuracy"] = d80Error <= dataset.Acceptance.MaximumD80RelativeError,
            ["coordinateAccuracy"] = coordinateError <= dataset.Acceptance.MaximumCoordinateErrorMetres,
            ["duration"] = total.Elapsed.TotalMilliseconds <= dataset.Acceptance.MaximumDurationMilliseconds,
            ["rejectedFrameFraction"] = rejected <= dataset.Acceptance.MaximumRejectedFrameFraction
        };
        var report = new E2EReport(measurementId, dataset.DatasetVersion, dataset.ModelVersion,
            dataset.CalibrationVersion, dataset.TransformVersion, DateTimeOffset.UtcNow,
            d50, d80, d50Error, d80Error, coordinateError,
            rejected, total.Elapsed.TotalMilliseconds, stages, checks, checks.Values.All(x => x));
        await WriteJson(Path.Combine(outputDirectory, "report.json"), report, cancellationToken);
        return report;
    }

    private static CapturedFrames SimulateCapture(GoldenDataset dataset)
    {
        var gray = new List<QualityFrame>();
        byte[]? rgb = null;
        for (var camera = 0; camera < 3; camera++)
        {
            var pixels = new byte[dataset.Width * dataset.Height];
            for (var y = 0; y < dataset.Height; y++)
                for (var x = 0; x < dataset.Width; x++)
                    pixels[y * dataset.Width + x] = (byte)(80 + ((x * 17 + y * 31 + camera * 7) % 140));
            gray.Add(new(((char)('A' + camera)).ToString(), dataset.Width, dataset.Height, pixels,
                dataset.HardwareTimestampNanoseconds + camera * 1_000_000));
            if (camera == 0)
                rgb = pixels.SelectMany(value => new[] { value, value, value }).ToArray();
        }
        return new(gray, rgb!);
    }

    private static async Task<QualityResult> AssessQuality(Guid id, IReadOnlyList<QualityFrame> frames, GoldenDataset dataset, CancellationToken token)
    {
        var options = new QualityOptions { DefaultSceneType = "pilot" };
        options.Scenes["pilot"] = new QualityThresholds { Version = $"quality-{dataset.DatasetVersion}", MinimumSharpness = 0.01, MinimumTexture = 0.05, MaximumLensContamination = 0.8 };
        return await new QualityAnalyzer(new InMemoryOptionsMonitor<QualityOptions>(options), new HeuristicLensContaminationModel(new InMemoryOptionsMonitor<QualityOptions>(options)), new InMemoryQualityResultStore())
            .AssessAsync(id, new QualityRequest("pilot", frames), token);
    }

    private static OrganizedPoint[] SimulateDepth(GoldenDataset dataset)
    {
        var points = new OrganizedPoint[dataset.Width * dataset.Height];
        for (var y = 0; y < dataset.Height; y++)
            for (var x = 0; x < dataset.Width; x++)
                points[y * dataset.Width + x] = new((x - dataset.Width / 2d) * .025, (y - dataset.Height / 2d) * .025,
                    dataset.DistanceMetres + ((x + y) % 3) * .002, .96);
        return points;
    }

    private static async Task<SegmentationData> Segment(Guid id, byte[] rgb, GoldenDataset dataset, string output, CancellationToken token)
    {
        var options = Options.Create(new SegmentationOptions { ModelVersion = dataset.ModelVersion, InputWidth = dataset.Width, InputHeight = dataset.Height, TileOverlap = 0 });
        using var backend = new DeterministicSegmentationBackend(options);
        var store = new SimulatorArtifactStore(output);
        var result = await new SegmentationProcessor(backend, store, options).ProcessAsync(id,
            new SegmentationRequest(new SegmentationFrame(dataset.Width, dataset.Height, 3, "RGB8", rgb)), token);
        var mask = ReadPgm(await File.ReadAllBytesAsync(store.Paths["segmentation/mask.pgm"], token));
        var confidenceBytes = ReadPgm(await File.ReadAllBytesAsync(store.Paths["segmentation/confidence.pgm"], token));
        return new(mask, confidenceBytes.Select(x => x / 255f).ToArray(), [.. store.Paths.Values]);
    }

    private static BlockAnalysisResult Analyze(Guid id, GoldenDataset dataset, IReadOnlyList<OrganizedPoint> points, SegmentationData segmentation)
    {
        var options = Options.Create(new BlockAnalysisOptions { MinimumPointsPerBlock = 8, MaximumNeighbourDistanceMetres = .08, PartialVisibilityBorderPixels = 0 });
        var refs = new AnalysisArtifactReferences(new("file:///point-cloud.json"), new("file:///mask.pgm"), new("file:///depth-confidence.pgm"), new("file:///segmentation-confidence.pgm"), dataset.CalibrationVersion);
        return new BlockAnalyzer(options).Analyze(id, new(dataset.Width, dataset.Height, points, segmentation.Mask,
            segmentation.Confidence, .98, "camera-rig", refs));
    }

    private static GeoreferenceResult Georeference(Guid id, GoldenDataset dataset, BlockAnalysisResult analysis)
    {
        var covariance = new double[36]; covariance[0] = covariance[7] = covariance[14] = .000001;
        var block = analysis.Blocks.Single(x => x.IsValid);
        var request = new GeoreferenceRequest(dataset.HardwareTimestampNanoseconds, "camera-rig", [],
            [new CameraBlock(block.BlockId, block.Centre, [], block.Geometry.EquivalentDiameterMillimetres, block.Confidence)],
            [new VersionedTransform("camera-rig", "vehicle", dataset.TransformVersion, new(Vector3.Zero, Quaternion.Identity), covariance)],
            new(dataset.HardwareTimestampNanoseconds, "vehicle", "mine-local", dataset.TransformVersion,
                new((float)(dataset.ExpectedPosition.XMetres - block.Centre.XMetres), (float)(dataset.ExpectedPosition.YMetres - block.Centre.YMetres), (float)(dataset.ExpectedPosition.ZMetres - block.Centre.ZMetres)), Quaternion.Identity, covariance));
        return new Georeferencer().Georeference(id, request);
    }

    private static byte[] ReadPgm(byte[] data)
    {
        var newlines = 0; var offset = 0;
        for (; offset < data.Length && newlines < 3; offset++) if (data[offset] == '\n') newlines++;
        return data[offset..];
    }

    private static double RelativeError(double actual, double expected) => Math.Abs(actual - expected) / expected;
    private static double Distance(LocalPoint point, GoldenPosition expected) => Math.Sqrt(Math.Pow(point.XMetres - expected.XMetres, 2) + Math.Pow(point.YMetres - expected.YMetres, 2) + Math.Pow(point.ZMetres - expected.ZMetres, 2));
    private static Guid DeterministicGuid(string value) => new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))[..16]);
    private static Task WriteJson<T>(string path, T value, CancellationToken token) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), token);
    private static void Validate(GoldenDataset dataset)
    {
        if (string.IsNullOrWhiteSpace(dataset.DatasetVersion) || string.IsNullOrWhiteSpace(dataset.ModelVersion) || string.IsNullOrWhiteSpace(dataset.CalibrationVersion) || dataset.Width < 4 || dataset.Height < 4)
            throw new InvalidOperationException("Dataset versions and dimensions are required.");
        if (dataset.DistanceMetres is < 12 or > 30) throw new InvalidOperationException("Pilot distance must be between 12 and 30 metres.");
    }

    private sealed record CapturedFrames(IReadOnlyList<QualityFrame> Gray, byte[] Rgb);
    private sealed record SegmentationData(byte[] Mask, float[] Confidence, IReadOnlyList<string> Artifacts);

    private sealed class SimulatorArtifactStore(string output) : ISegmentationArtifactStore
    {
        public Dictionary<string, string> Paths { get; } = new(StringComparer.Ordinal);
        public async Task<Uri> PutAsync(Guid measurementId, string path, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            var target = Path.Combine(output, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, content.ToArray(), cancellationToken);
            Paths[path] = target;
            return new Uri(Path.GetFullPath(target));
        }
    }
}
