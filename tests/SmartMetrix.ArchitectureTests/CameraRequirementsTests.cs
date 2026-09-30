using System.Net;
using Microsoft.Extensions.Options;
using SmartMetrix.CameraService;

namespace SmartMetrix.ArchitectureTests;

public sealed class CameraRequirementsTests
{
    [Theory]
    [Trait("Requirement", "CAM-01")]
    [InlineData("missing", "FrameSetIncomplete")]
    [InlineData("duplicate", "FrameSetIncomplete")]
    [InlineData("empty", "FrameSetIncomplete")]
    [InlineData("skew", "TimestampSkewExceeded")]
    public async Task InvalidSetIsRejectedBeforeAnyStorageWrite(string defect, string expectedCode)
    {
        var frames = Frames();
        if (defect == "missing") frames.RemoveAt(2);
        if (defect == "duplicate") frames[2] = frames[2] with { CameraId = "A" };
        if (defect == "empty") frames[2] = frames[2] with { Payload = ReadOnlyMemory<byte>.Empty };
        if (defect == "skew") frames[2] = frames[2] with { HardwareTimestampNanoseconds = 102 };
        using var handler = new StorageHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://storage/") };
        var coordinator = new CaptureCoordinator(new Adapter(frames), new CameraStorageClient(client),
            Options.Create(new CameraOptions { MaximumTimestampSkewNanoseconds = 1 }));

        var error = await Assert.ThrowsAsync<CameraCaptureException>(() =>
            coordinator.CaptureAsync(Guid.NewGuid(), new(), CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    [Trait("Requirement", "CAM-02")]
    public async Task StorageFailureAbortsCaptureWithDiagnosticError()
    {
        using var handler = new StorageHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://storage/") };
        var coordinator = new CaptureCoordinator(new Adapter(Frames()), new CameraStorageClient(client),
            Options.Create(new CameraOptions()));

        var error = await Assert.ThrowsAsync<CameraCaptureException>(() =>
            coordinator.CaptureAsync(Guid.NewGuid(), new(), CancellationToken.None));

        Assert.Equal("StorageFailure", error.Code);
        Assert.Equal(502, error.StatusCode);
        Assert.Equal(1, handler.Requests);
    }

    private static List<CapturedFrame> Frames() => [
        new("A", 1, 100, "application/octet-stream", new byte[] { 1 }),
        new("B", 1, 100, "application/octet-stream", new byte[] { 2 }),
        new("C", 1, 100, "application/octet-stream", new byte[] { 3 })];

    private sealed class Adapter(IReadOnlyList<CapturedFrame> frames) : ICameraAdapter
    {
        public string Name => "Test";
        public Task<IReadOnlyList<CapturedFrame>> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult(frames);
    }

    private sealed class StorageHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
