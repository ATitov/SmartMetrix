using Microsoft.Extensions.Options;
using SmartMetrix.BlockAnalysisService;

namespace SmartMetrix.ArchitectureTests;

public sealed class BlockAnalysisTests
{
    [Fact]
    public void CalculatesControlledVolumeWeightedPercentilesAndProvenance()
    {
        var analyzer = Analyzer(2); var request = Scene(); var id = Guid.NewGuid();
        var result = analyzer.Analyze(id, request);
        Assert.Equal(id, result.MeasurementId); Assert.Equal("millimetre", result.LengthUnit);
        Assert.Equal(2, result.Blocks.Count(x => x.IsValid)); Assert.Equal(result.D50Millimetres, result.D80Millimetres, 8);
        Assert.True(result.D80Millimetres > result.D10Millimetres); Assert.Equal("test-v1", result.Provenance.Version);
        Assert.All(result.Blocks, b => { Assert.Equal(id, b.MeasurementId); Assert.Same(request.Artifacts, b.SourceArtifacts); });
    }

    [Fact]
    public void SplitsOnCrackMarksBorderBlockAndReportsConfidenceReasons()
    {
        var result = Analyzer(2).Analyze(Guid.NewGuid(), Scene(calibration: .5));
        Assert.Contains(result.Blocks, x => x.IsPartiallyVisible && x.QualityReasons.Contains(BlockQualityReasons.PartialVisibility));
        Assert.Contains(BlockQualityReasons.LowCalibrationConfidence, result.QualityReasons);
        Assert.All(result.Blocks, x => Assert.InRange(x.Confidence, 0, 1));
    }

    [Fact]
    public void InvalidShapeIsRejected() => Assert.Throws<ArgumentException>(() => Analyzer(1).Analyze(Guid.NewGuid(), Scene() with { Mask = [1] }));

    private static BlockAnalyzer Analyzer(int minimum) => new(Options.Create(new BlockAnalysisOptions { MinimumPointsPerBlock = minimum, MaximumNeighbourDistanceMetres = .3, OutlierDistanceFactor = 10, PartialVisibilityBorderPixels = 1, AlgorithmVersion = "test-v1" }));
    private static BlockAnalysisRequest Scene(double calibration = 1)
    {
        const int w = 7, h = 3; var points = new OrganizedPoint[w * h]; var mask = new byte[w * h]; var confidence = Enumerable.Repeat(1f, w * h).ToArray();
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) { var i = y * w + x; var px = x < 4 ? x * .1 : .8 + (x - 4) * .2; points[i] = new(px, y * .1, (x < 3 ? 1 : 2) + y * .05, .9); if (x != 3) mask[i] = 1; }
        return new(w, h, points, mask, confidence, calibration, "rig:test", new(new("s3://a/cloud"), new("s3://a/mask"), new("s3://a/depth"), new("s3://a/seg"), "cal-v1"));
    }
}
