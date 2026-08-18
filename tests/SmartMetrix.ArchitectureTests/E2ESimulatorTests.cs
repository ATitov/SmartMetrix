using SmartMetrix.E2ESimulator;

namespace SmartMetrix.ArchitectureTests;

public sealed class E2ESimulatorTests
{
    [Fact]
    public async Task GoldenDatasetProducesReproduciblePassDecision()
    {
        var root = FindRepositoryRoot();
        var dataset = Path.Combine(root, "tools", "SmartMetrix.E2ESimulator", "dataset", "golden-v1.json");
        var output = Path.Combine(Path.GetTempPath(), "smartmetrix-e2e", Guid.NewGuid().ToString("N"));

        var first = await E2ESimulatorRunner.RunAsync(dataset, output);
        var second = await E2ESimulatorRunner.RunAsync(dataset, output);

        Assert.True(first.Passed);
        Assert.Equal(first.MeasurementId, second.MeasurementId);
        Assert.Equal(first.D50Millimetres, second.D50Millimetres);
        Assert.Equal(first.D80Millimetres, second.D80Millimetres);
        Assert.Equal(first.CoordinateErrorMetres, second.CoordinateErrorMetres);
        Assert.Equal(["capture", "quality", "depth", "segmentation", "analysis", "georeference"], first.Stages.Select(x => x.Stage));
        Assert.All(first.Stages, stage => Assert.False(string.IsNullOrWhiteSpace(stage.Version)));
        Assert.True(File.Exists(Path.Combine(output, "report.json")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SmartMetrix.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
