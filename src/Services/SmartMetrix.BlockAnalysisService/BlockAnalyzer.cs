using Microsoft.Extensions.Options;
using SmartMetrix.Domain;

namespace SmartMetrix.BlockAnalysisService;

public sealed class BlockAnalyzer(IOptions<BlockAnalysisOptions> configured)
{
    private static readonly (int X, int Y)[] Neighbours = [(-1, 0), (1, 0), (0, -1), (0, 1)];
    private static readonly double[] PercentileFractions = [.10, .50, .80, .95];
    private readonly BlockAnalysisOptions _options = configured.Value.WithDefaults();

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
        var hasVolumes = valid.Any(x => x.Geometry.HasVolumeEstimate);
        var percentiles = PercentileFractions.Select(p => hasVolumes ? (double?)VolumeWeightedPercentile(valid, p) : null).ToArray();
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
        var parameters = (Dictionary<string, double>)provenance.Parameters;
        parameters["minimumDepthConfidence"] = _options.MinimumDepthConfidence;
        parameters["minimumSegmentationConfidence"] = _options.MinimumSegmentationConfidence;
        parameters["minimumCalibrationConfidence"] = _options.MinimumCalibrationConfidence;
        parameters["partialVisibilityConfidenceFactor"] = _options.PartialVisibilityConfidenceFactor;
        parameters["minimumThicknessMillimetres"] = _options.MinimumThicknessMillimetres;
        parameters["oversizeThresholdMillimetres"] = _options.OversizeThresholdMillimetres;
        parameters["partialVisibilityBorderPixels"] = _options.PartialVisibilityBorderPixels;
        for (var i = 0; i < _options.SizeClassBoundariesMillimetres.Length; i++)
            parameters[$"sizeClassBoundary{i}Millimetres"] = _options.SizeClassBoundariesMillimetres[i];
        return new(measurementId, "millimetre", "square-millimetre", "cubic-millimetre", blocks,
            SizeClasses(valid), percentiles[0], percentiles[1], percentiles[2], percentiles[3],
            valid.Count(x => x.Geometry.EquivalentDiameterMillimetres >= _options.OversizeThresholdMillimetres),
            confidence, reasons, provenance)
        {
            CrackPixelCount = request.CracksSupported ? request.Mask.Count(x => x == (byte)AnalysisMaskClass.Crack) : null,
            CrackImageFraction = request.CracksSupported ? request.Mask.Count(x => x == (byte)AnalysisMaskClass.Crack) / (double)request.Mask.Count : null
        };
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
        if (depth < _options.MinimumDepthConfidence) reasons.Add(BlockQualityReasons.LowDepthConfidence);
        if (segmentation < _options.MinimumSegmentationConfidence) reasons.Add(BlockQualityReasons.LowSegmentationConfidence);
        if (calibration < _options.MinimumCalibrationConfidence) reasons.Add(BlockQualityReasons.LowCalibrationConfidence);
        var confidence = Math.Pow(Math.Max(0, depth * segmentation * calibration), 1d / 3) * (partial ? _options.PartialVisibilityConfidenceFactor : 1);
        var geometry = SurfaceGeometry.Calculate(points, partial, _options.MinimumThicknessMillimetres);
        if (!geometry.HasVolumeEstimate) reasons.Add(BlockQualityReasons.VolumeUnavailable);
        var retained = retainedPixels.ToHashSet();
        var boundary = retainedPixels.Where(i => Neighbours.Any(n =>
        {
            var x = i % request.Width + n.X; var y = i / request.Width + n.Y;
            return x < 0 || y < 0 || x >= request.Width || y >= request.Height || !retained.Contains(y * request.Width + x);
        })).Select(i => new LocalPoint(request.Points[i].XMetres, request.Points[i].YMetres, request.Points[i].ZMetres)).ToArray();
        var c = Centre(points.Length == 0 ? original : points);
        return new(Guid.NewGuid(), measurementId, request.CoordinateSystemId, new(c.XMetres, c.YMetres, c.ZMetres), geometry,
            points.Length >= _options.MinimumPointsPerBlock, partial, confidence, reasons.Order().ToArray(), request.Artifacts,
            request.InstanceLabels?[pixels[0]])
        { Boundary = boundary };
    }

    public static double VolumeWeightedPercentile(IEnumerable<AnalysedBlock> blocks, double fraction)
    {
        var ordered = blocks.Where(x => x.IsValid && x.Geometry.VolumeCubicMillimetres > 0).OrderBy(x => x.Geometry.EquivalentDiameterMillimetres).ToArray();
        var total = ordered.Sum(x => x.Geometry.VolumeCubicMillimetres); if (total <= 0) return 0;
        var target = Math.Clamp(fraction, 0, 1) * total; var cumulative = 0d;
        foreach (var block in ordered) { cumulative += block.Geometry.VolumeCubicMillimetres; if (cumulative >= target) return block.Geometry.EquivalentDiameterMillimetres; }
        return ordered[^1].Geometry.EquivalentDiameterMillimetres;
    }

    private Dictionary<string, int> SizeClasses(IEnumerable<AnalysedBlock> blocks)
    {
        var result = new Dictionary<string, int>();
        var low = 0d;
        foreach (var high in _options.SizeClassBoundariesMillimetres)
        {
            result[FormattableString.Invariant($"{low}-{high} mm")] = blocks.Count(x => x.Geometry.EquivalentDiameterMillimetres >= low && x.Geometry.EquivalentDiameterMillimetres < high);
            low = high;
        }
        result[FormattableString.Invariant($">={low} mm")] = blocks.Count(x => x.Geometry.EquivalentDiameterMillimetres >= low);
        return result;
    }
    private static OrganizedPoint Centre(OrganizedPoint[] p) => p.Length == 0 ? default : new(p.Average(x => x.XMetres), p.Average(x => x.YMetres), p.Average(x => x.ZMetres), p.Average(x => x.DepthConfidence));
    private static double Distance(OrganizedPoint a, OrganizedPoint b) => Math.Sqrt(Math.Pow(a.XMetres - b.XMetres, 2) + Math.Pow(a.YMetres - b.YMetres, 2) + Math.Pow(a.ZMetres - b.ZMetres, 2));
    private static void Validate(BlockAnalysisRequest request)
    {
        if (!double.IsFinite(request.CalibrationConfidence) || request.CalibrationConfidence is < 0 or > 1 ||
            request.SegmentationConfidence.Any(x => !float.IsFinite(x) || x is < 0 or > 1))
            throw new ArgumentException("Confidence values must be finite and in [0,1].");
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
