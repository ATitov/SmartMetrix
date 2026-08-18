using Microsoft.Extensions.Options;
using SmartMetrix.CameraService;

namespace SmartMetrix.ArchitectureTests;

public sealed class CameraCaptureTests
{
    [Fact]
    public async Task SimulatorReturnsExactlyThreeDeterministicSynchronizedFrames()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var options = Options.Create(new CameraOptions
        {
            Adapter = "Simulator",
            SimulatorFrameDirectory = Path.Combine(root, "src", "Services", "SmartMetrix.CameraService", "simulator-frames")
        });
        var environment = new TestEnvironment(root);
        var adapter = new SimulatorCameraAdapter(options, environment);

        var first = await adapter.CaptureAsync(CancellationToken.None);
        var second = await adapter.CaptureAsync(CancellationToken.None);

        Assert.Equal(["A", "B", "C"], first.Select(frame => frame.CameraId));
        Assert.All(first, frame => Assert.False(frame.Payload.IsEmpty));
        Assert.Equal(1, first[0].FrameId);
        Assert.Equal(2, second[0].FrameId);
        Assert.Equal(first.Select(frame => frame.HardwareTimestampNanoseconds), second.Select(frame => frame.HardwareTimestampNanoseconds));
    }

    private sealed class TestEnvironment(string contentRoot) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "SmartMetrix.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
