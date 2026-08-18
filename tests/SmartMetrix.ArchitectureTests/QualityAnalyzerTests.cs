using Microsoft.Extensions.Options;
using SmartMetrix.QualityService;

namespace SmartMetrix.ArchitectureTests;

public sealed class QualityAnalyzerTests
{
    [Fact]
    public async Task MissingFrameIsAlwaysRejected()
    {
        var analyzer = CreateAnalyzer();
        var frame = Frame("A", 1);
        var result = await analyzer.AssessAsync(Guid.NewGuid(), new QualityRequest("default", [frame]));
        Assert.False(result.Accepted);
        Assert.Contains(QualityReasonCodes.MissingFrame, result.ReasonCodes);
    }

    [Fact]
    public async Task ReferenceFramesProduceReproducibleNormalizedMetrics()
    {
        var analyzer = CreateAnalyzer();
        var request = new QualityRequest("default", [Frame("A", 1), Frame("B", 2), Frame("C", 3)]);
        var first = await analyzer.AssessAsync(Guid.NewGuid(), request);
        var second = await analyzer.AssessAsync(Guid.NewGuid(), request);
        Assert.Equal(first.Frames, second.Frames);
        Assert.All(first.Frames.SelectMany(Metrics), value => Assert.InRange(value, 0, 1));
        Assert.Equal("test-1", first.ThresholdVersion);
    }

    [Fact]
    public async Task TimestampMismatchHasMachineReadableReason()
    {
        var analyzer = CreateAnalyzer(maxSkew: 1);
        var result = await analyzer.AssessAsync(Guid.NewGuid(), new("default", [Frame("A", 1), Frame("B", 3), Frame("C", 5)]));
        Assert.Contains(QualityReasonCodes.FramesNotSynchronized, result.ReasonCodes);
    }

    [Fact]
    public async Task DuplicateCameraIsReportedAsInconsistentSet()
    {
        var analyzer = CreateAnalyzer();
        var result = await analyzer.AssessAsync(Guid.NewGuid(), new("default", [Frame("A", 1), Frame("A", 1), Frame("B", 1), Frame("C", 1)]));
        Assert.Contains(QualityReasonCodes.InconsistentFrameSet, result.ReasonCodes);
        Assert.DoesNotContain(QualityReasonCodes.MissingFrame, result.ReasonCodes);
    }

    private static QualityAnalyzer CreateAnalyzer(long maxSkew = 10) => new(
        new StaticOptions(new QualityOptions { Scenes = { ["default"] = new() { Version = "test-1", MaximumTimestampSkewNanoseconds = maxSkew } } }),
        new FixedContaminationModel(), new InMemoryQualityResultStore());

    private static QualityFrame Frame(string camera, long timestamp)
    {
        var pixels = Enumerable.Range(0, 64).Select(x => (byte)((x * 37) % 220 + 18)).ToArray();
        return new(camera, 8, 8, pixels, timestamp);
    }

    private static IEnumerable<double> Metrics(FrameQualityMetrics x) =>
        [x.Sharpness, x.Exposure, x.Saturation, x.Texture, x.ShadowFraction, x.LensContamination];

    private sealed class FixedContaminationModel : ILensContaminationModel
    {
        public ValueTask<double> PredictAsync(QualityFrame frame, CancellationToken cancellationToken = default) => ValueTask.FromResult(0.1);
    }

    private sealed class StaticOptions(QualityOptions value) : IOptionsMonitor<QualityOptions>
    {
        public QualityOptions CurrentValue => value;
        public QualityOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<QualityOptions, string?> listener) => null;
    }
}
