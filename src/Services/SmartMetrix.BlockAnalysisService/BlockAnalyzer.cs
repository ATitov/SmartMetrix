using Microsoft.Extensions.Options;
using SmartMetrix.Domain;

namespace SmartMetrix.BlockAnalysisService;

public sealed class BlockAnalyzer(IOptions<BlockAnalysisOptions> configured)
{
    private static readonly (int X, int Y)[] Neighbours = [(-1, 0), (1, 0), (0, -1), (0, 1)];
    private static readonly double[] PercentileFractions = [.10, .50, .80, .95];
    private readonly BlockAnalysisOptions _options = configured.Value;

    public BlockAnalysisResult Analyze(Guid measurementId, BlockAnalysisRequest request)
    {
        Validate(request);
        var visited = new bool[request.Mask.Count];
        var blocks = new List<AnalysedBlock>();
        for (var seed = 0; seed < visited.Length; seed++)
        {
            if (visited[seed] || request.Mask[seed] != (byte)AnalysisMaskClass.Rock || !request.Points[seed].IsValid) continue;
            var pixels = Flood(seed, request, visited);
            blocks.Add(CreateBlock(measurementId, request, pixels));
        }

        var valid = blocks.Where(x => x.IsValid).ToArray();
        var percentiles = PercentileFractions.Select(p => VolumeWeightedPercentile(valid, p)).ToArray();
        var reasons = blocks.SelectMany(x => x.QualityReasons).Distinct(StringComparer.Ordinal).Order().ToList();
        if (valid.Length == 0) reasons.Add(BlockQualityReasons.NoValidBlocks);
        var confidence = valid.Length == 0 ? 0 : valid.Average(x => x.Confidence);
        var provenance = new AlgorithmProvenance("SmartMetrix.BlockAnalysis", _options.AlgorithmVersion,
            new Dictionary<string, double>
            {
                ["minimumPointsPerBlock"] = _options.MinimumPointsPerBlock,
                ["maximumNeighbourDistanceMetres"] = _options.MaximumNeighbourDistanceMetres,
                ["outlierDistanceFactor"] = _options.OutlierDistanceFactor,
                ["usesInstanceLabels"] = request.InstanceLabels is null ? 0 : 1
            });
        return new(measurementId, "millimetre", "square-millimetre", "cubic-millimetre", blocks,
            SizeClasses(valid), percentiles[0], percentiles[1], percentiles[2], percentiles[3],
            valid.Count(x => x.Geometry.EquivalentDiameterMillimetres >= _options.OversizeThresholdMillimetres),
            confidence, reasons, provenance);
    }

    private List<int> Flood(int seed, BlockAnalysisRequest request, bool[] visited)
    {
        var result = new List<int>(); var queue = new Queue<int>(); queue.Enqueue(seed); visited[seed] = true;
        while (queue.TryDequeue(out var index))
        {
            result.Add(index); var x = index % request.Width; var y = index / request.Width;
            foreach (var offset in Neighbours)
            {
                var nx = x + offset.X; var ny = y + offset.Y;
                if (nx < 0 || ny < 0 || nx >= request.Width || ny >= request.Height) continue;
                var next = ny * request.Width + nx;
                if (visited[next] || request.Mask[next] != (byte)AnalysisMaskClass.Rock || !request.Points[next].IsValid) continue;
                if (request.InstanceLabels is { } labels && labels[index] != labels[next]) continue;
                if (Distance(request.Points[index], request.Points[next]) > _options.MaximumNeighbourDistanceMetres) continue;
                visited[next] = true; queue.Enqueue(next);
            }
        }
        return result;
    }

    private AnalysedBlock CreateBlock(Guid measurementId, BlockAnalysisRequest request, List<int> pixels)
    {
        var original = pixels.Select(i => request.Points[i]).ToArray();
        var centre = Centre(original); var distances = original.Select(p => Distance(p, centre)).Order().ToArray();
        var median = distances[distances.Length / 2];
        var retainedPixels = pixels.Where(i => median == 0 || Distance(request.Points[i], centre) <= median * _options.OutlierDistanceFactor).ToArray();
        var points = retainedPixels.Select(i => request.Points[i]).ToArray();
        var reasons = new List<string>();
        if (points.Length < original.Length) reasons.Add(BlockQualityReasons.OutliersRemoved);
        var partial = retainedPixels.Any(i => { var x = i % request.Width; var y = i / request.Width; var b = _options.PartialVisibilityBorderPixels; return x < b || y < b || x >= request.Width - b || y >= request.Height - b; });
        if (partial) reasons.Add(BlockQualityReasons.PartialVisibility);
        if (points.Length < _options.MinimumPointsPerBlock) reasons.Add(BlockQualityReasons.TooFewPoints);
        var depth = points.Length == 0 ? 0 : points.Average(x => Math.Clamp(x.DepthConfidence, 0, 1));
        var segmentation = retainedPixels.Length == 0 ? 0 : retainedPixels.Average(i => Math.Clamp(request.SegmentationConfidence[i], 0, 1));
        var calibration = Math.Clamp(request.CalibrationConfidence, 0, 1);
        if (depth < .6) reasons.Add(BlockQualityReasons.LowDepthConfidence);
        if (segmentation < .6) reasons.Add(BlockQualityReasons.LowSegmentationConfidence);
        if (calibration < .8) reasons.Add(BlockQualityReasons.LowCalibrationConfidence);
        var confidence = Math.Pow(Math.Max(0, depth * segmentation * calibration), 1d / 3) * (partial ? .75 : 1);
        var geometry = Geometry(points);
        var c = Centre(points.Length == 0 ? original : points);
        return new(Guid.NewGuid(), measurementId, request.CoordinateSystemId, new(c.XMetres, c.YMetres, c.ZMetres), geometry,
            points.Length >= _options.MinimumPointsPerBlock, partial, confidence, reasons.Order().ToArray(), request.Artifacts,
            request.InstanceLabels?[pixels[0]]);
    }

    private static BlockGeometry Geometry(OrganizedPoint[] points)
    {
        if (points.Length == 0) return new(0, 0, 0, 0, 0, 0);
        var axes = new[] { points.Max(p => p.XMetres) - points.Min(p => p.XMetres), points.Max(p => p.YMetres) - points.Min(p => p.YMetres), points.Max(p => p.ZMetres) - points.Min(p => p.ZMetres) }.OrderDescending().Select(x => x * 1000).ToArray();
        var a = Math.Max(axes[0], .001); var b = Math.Max(axes[1], .001); var c = Math.Max(axes[2], .001);
        var volume = Math.PI / 6 * a * b * c; var equivalent = Math.Cbrt(6 * volume / Math.PI); var area = Math.PI / 4 * a * b;
        return new(a, b, c, equivalent, area, volume);
    }

    public static double VolumeWeightedPercentile(IEnumerable<AnalysedBlock> blocks, double fraction)
    {
        var ordered = blocks.Where(x => x.IsValid && x.Geometry.VolumeCubicMillimetres > 0).OrderBy(x => x.Geometry.EquivalentDiameterMillimetres).ToArray();
        var total = ordered.Sum(x => x.Geometry.VolumeCubicMillimetres); if (total <= 0) return 0;
        var target = Math.Clamp(fraction, 0, 1) * total; var cumulative = 0d;
        foreach (var block in ordered) { cumulative += block.Geometry.VolumeCubicMillimetres; if (cumulative >= target) return block.Geometry.EquivalentDiameterMillimetres; }
        return ordered[^1].Geometry.EquivalentDiameterMillimetres;
    }

    private static Dictionary<string, int> SizeClasses(IEnumerable<AnalysedBlock> blocks) =>
        new Dictionary<string, int> { ["0-100 mm"] = blocks.Count(x => x.Geometry.EquivalentDiameterMillimetres < 100), ["100-300 mm"] = blocks.Count(x => x.Geometry.EquivalentDiameterMillimetres >= 100 && x.Geometry.EquivalentDiameterMillimetres < 300), ["300-600 mm"] = blocks.Count(x => x.Geometry.EquivalentDiameterMillimetres >= 300 && x.Geometry.EquivalentDiameterMillimetres < 600), [">=600 mm"] = blocks.Count(x => x.Geometry.EquivalentDiameterMillimetres >= 600) };
    private static OrganizedPoint Centre(OrganizedPoint[] p) => p.Length == 0 ? default : new(p.Average(x => x.XMetres), p.Average(x => x.YMetres), p.Average(x => x.ZMetres), p.Average(x => x.DepthConfidence));
    private static double Distance(OrganizedPoint a, OrganizedPoint b) => Math.Sqrt(Math.Pow(a.XMetres - b.XMetres, 2) + Math.Pow(a.YMetres - b.YMetres, 2) + Math.Pow(a.ZMetres - b.ZMetres, 2));
    private static void Validate(BlockAnalysisRequest request)
    {
        if (request.Width <= 0 || request.Height <= 0 || (long)request.Width * request.Height != request.Points.Count || request.Points.Count != request.Mask.Count || request.Mask.Count != request.SegmentationConfidence.Count) throw new ArgumentException("Point cloud, mask and confidence map must be non-empty organized arrays of equal dimensions.");
        if (string.IsNullOrWhiteSpace(request.CoordinateSystemId)) throw new ArgumentException("Coordinate system is required.");
        if (request.Mask.Any(x => x > (byte)AnalysisMaskClass.Crack)) throw new ArgumentException("Mask contains an unsupported class.");
        if (request.InstanceLabels is { } labels)
        {
            if (labels.Count != request.Mask.Count) throw new ArgumentException("Instance labels must match the organized image dimensions.");
            for (var i = 0; i < labels.Count; i++)
                if (labels[i] < 0 || (labels[i] > 0) != (request.Mask[i] == (byte)AnalysisMaskClass.Rock))
                    throw new ArgumentException("Instance labels must be positive on rock pixels and zero elsewhere.");
        }
    }
}
