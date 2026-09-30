using SmartMetrix.LocalPositioningService;

namespace SmartMetrix.ArchitectureTests;

public sealed class PositioningBufferRequirementsTests
{
    [Fact]
    [Trait("Requirement", "POS-03")]
    public void RetainsNewestTenThousandSamplesInTimestampOrderAndIsolatesRigs()
    {
        var buffer = new PositioningSampleBuffer();
        buffer.Add("rig-a", Enumerable.Range(0, 10000).Reverse().Select(Sample).ToArray());
        var snapshot = buffer.Get("rig-a");
        buffer.Add("rig-a", [Sample(10000)]);
        buffer.Add("rig-b", [Sample(42)]);

        Assert.Equal(Enumerable.Range(1, 10000).Select(x => (long)x),
            buffer.Get("rig-a").Select(x => x.HardwareTimestampNanoseconds));
        Assert.Equal(0, snapshot[0].HardwareTimestampNanoseconds);
        Assert.Equal(42, Assert.Single(buffer.Get("rig-b")).HardwareTimestampNanoseconds);
        Assert.Empty(buffer.Get("unknown"));
    }

    [Theory]
    [Trait("Requirement", "POS-03")]
    [InlineData(0)]
    [InlineData(10001)]
    public void InvalidBatchDoesNotModifyExistingWindow(int count)
    {
        var buffer = new PositioningSampleBuffer();
        buffer.Add("rig", [Sample(1)]);
        Assert.Throws<ArgumentException>(() => buffer.Add("rig", Enumerable.Range(0, count).Select(Sample).ToArray()));
        Assert.Equal(1, Assert.Single(buffer.Get("rig")).HardwareTimestampNanoseconds);
    }

    private static PositioningSample Sample(int timestamp) => new(PositionSourceType.Imu, "imu", timestamp, null, null, new double[36]);
}
