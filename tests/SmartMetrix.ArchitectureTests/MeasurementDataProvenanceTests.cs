using System.Text.Json;
using SmartMetrix.MeasurementOrchestrator;

namespace SmartMetrix.ArchitectureTests;

public sealed class MeasurementDataProvenanceTests
{
    [Theory]
    [InlineData("Arena", false, false, false)]
    [InlineData("Arena", true, false, true)]
    [InlineData("Arena", false, true, true)]
    [InlineData("Rtsp", true, false, true)]
    [InlineData("Rtsp", false, false, true)]
    [InlineData("Simulator", false, false, true)]
    [InlineData("Unknown", false, false, true)]
    public void TestOriginCannotBeLostInFinalManifest(string adapter, bool cameraTest, bool segmentationTest, bool expected)
    {
        var capture = JsonSerializer.SerializeToElement(new { adapter, isTestData = cameraTest });
        var segmentation = JsonSerializer.SerializeToElement(new { isTestData = segmentationTest });
        Assert.Equal(expected, MeasurementDataProvenance.IsTestData(capture, segmentation));
    }

    [Fact]
    public void MissingProvenanceIsConservativelyTestData()
    {
        var unknown = JsonSerializer.SerializeToElement(new { adapter = "Arena" });
        var known = JsonSerializer.SerializeToElement(new { adapter = "Arena", isTestData = false });
        Assert.True(MeasurementDataProvenance.IsTestData(unknown, known));
        Assert.True(MeasurementDataProvenance.IsTestData(known, unknown));
    }
}
