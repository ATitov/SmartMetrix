using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Options;
using SmartMetrix.CameraService;

namespace SmartMetrix.ArchitectureTests;

public sealed class RtspCameraTests
{
    [Fact]
    public async Task SingleCameraCaptureNeedsOnlyItsUrlAndPreservesPixelsWithoutHardwareTime()
    {
        using var adapter = new RtspCameraAdapter(Settings(), new Reader((_, _) => Task.FromResult(Image())));
        var frame = await adapter.CaptureCameraAsync("a", CancellationToken.None);
        Assert.Equal("A", frame.CameraId);
        Assert.Equal(0, frame.HardwareTimestampNanoseconds);
        Assert.Equal("HostReceive", frame.TimestampSource);
        Assert.NotNull(frame.ReceivedAt);
        Assert.Equal(2, frame.Width);
        Assert.Equal(Image().Pixels, frame.Payload.ToArray());
        var error = await Assert.ThrowsAsync<CameraCaptureException>(() => adapter.CaptureAsync(CancellationToken.None));
        Assert.Equal("NotConfigured", error.Code);
    }

    [Fact]
    [Trait("Requirement", "CAM-03")]
    public async Task ThreeStreamsOpenConcurrentlyAndStoredResultDoesNotClaimSynchronization()
    {
        var opened = 0;
        var allOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var adapter = new RtspCameraAdapter(Settings(all: true), new Reader(async (_, ct) =>
        {
            if (Interlocked.Increment(ref opened) == 3) allOpened.SetResult();
            await allOpened.Task.WaitAsync(ct);
            return Image();
        }));
        using var handler = new StorageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://storage") };
        var coordinator = new CaptureCoordinator(adapter, new CameraStorageClient(http), Settings(all: true));
        var response = await coordinator.CaptureAsync(Guid.NewGuid(), new CaptureRequest(), CancellationToken.None);
        Assert.Equal(3, opened);
        Assert.Equal(["A", "B", "C"], response.Frames.Select(frame => frame.CameraId));
        Assert.True(response.IsTestData);
        Assert.Null(response.TimestampSkewNanoseconds);
        Assert.Null(response.ExposedAt);
        Assert.All(response.Frames, frame =>
        {
            Assert.Equal("HostReceive", frame.TimestampSource);
            Assert.Equal(0, frame.HardwareTimestampNanoseconds);
            Assert.Equal(2, frame.Width);
        });
        Assert.Equal(3, handler.Writes);
    }

    [Fact]
    public async Task TimeoutAndCallerCancellationAreDistinguished()
    {
        using var adapter = new RtspCameraAdapter(Settings(timeout: 50), new Reader(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Image();
        }));
        var error = await Assert.ThrowsAsync<CameraCaptureException>(() => adapter.CaptureCameraAsync("A", CancellationToken.None));
        Assert.Equal("CaptureTimeout", error.Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.CaptureCameraAsync("A", cancellation.Token));
    }

    [Fact]
    public async Task FailureCancelsOtherStreamsAndDoesNotReturnPartialSet()
    {
        var opened = 0;
        var allOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = 0;
        using var adapter = new RtspCameraAdapter(Settings(all: true), new Reader(async (url, ct) =>
        {
            if (Interlocked.Increment(ref opened) == 3) allOpened.SetResult();
            await allOpened.Task.WaitAsync(ct);
            if (url.Contains("camera-a")) throw new CameraCaptureException("RtspCaptureFailed", "Unavailable", 502);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            return Image();
        }));
        var error = await Assert.ThrowsAsync<CameraCaptureException>(() => adapter.CaptureAsync(CancellationToken.None));
        Assert.Equal("RtspCaptureFailed", error.Code);
        Assert.Equal(2, cancelled);
    }

    [Theory]
    [InlineData(false, "rtsp://user:secret@camera-a/stream")]
    [InlineData(true, "https://user:secret@camera-a/stream")]
    public async Task InvalidConfigurationNeverOpensReaderOrDisclosesCredentials(bool testMode, string url)
    {
        var settings = Options.Create(new CameraOptions { Rtsp = new() { TestMode = testMode, CameraAUrl = url } });
        using var adapter = new RtspCameraAdapter(settings, new Reader((_, _) => throw new InvalidOperationException("Must not run")));
        var error = await Assert.ThrowsAsync<CameraCaptureException>(() => adapter.CaptureCameraAsync("A", CancellationToken.None));
        Assert.Equal("NotConfigured", error.Code);
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain(url, error.Message);
    }

    [Fact]
    public async Task MismatchedDimensionsAreRejected()
    {
        using var adapter = new RtspCameraAdapter(Settings(all: true), new Reader((url, _) =>
            Task.FromResult(url.Contains("camera-b") ? new RtspDecodedFrame(1, 4, Image().Pixels) : Image())));
        var error = await Assert.ThrowsAsync<CameraCaptureException>(() => adapter.CaptureAsync(CancellationToken.None));
        Assert.Equal("FrameSizeMismatch", error.Code);
    }

    [Fact]
    public async Task MissingFfmpegReturnsSanitizedConfigurationError()
    {
        var settings = Options.Create(new CameraOptions { Rtsp = new() { FfmpegPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "ffmpeg") } });
        var reader = new FfmpegRtspFrameReader(settings);
        var error = await Assert.ThrowsAsync<CameraCaptureException>(() => reader.ReadAsync("rtsp://user:secret@camera/stream", CancellationToken.None));
        Assert.Equal("NotConfigured", error.Code);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public void PgmDecoderPreservesWhitespaceValuedPixelsAndRejectsTruncatedOrOversizedFrames()
    {
        byte[] pixels = [10, 13, 32, 0];
        byte[] pgm = [.. Encoding.ASCII.GetBytes("P5\n2 2\n255\n"), .. pixels];
        var image = FfmpegRtspFrameReader.DecodePgm(pgm, 4);
        Assert.Equal(pixels, image.Pixels);
        Assert.Equal(2, image.Width);
        Assert.Throws<CameraCaptureException>(() => FfmpegRtspFrameReader.DecodePgm(pgm[..^1], 4));
        Assert.Throws<CameraCaptureException>(() => FfmpegRtspFrameReader.DecodePgm(pgm, 3));
        Assert.Throws<CameraCaptureException>(() => FfmpegRtspFrameReader.DecodePgm(Encoding.ASCII.GetBytes("P5\n999999999 999999999\n255\n"), 4));
        Assert.Throws<CameraCaptureException>(() => FfmpegRtspFrameReader.DecodePgm([], 4));
    }

    private static RtspDecodedFrame Image() => new(2, 2, [0, 10, 128, 255]);

    private static IOptions<CameraOptions> Settings(bool all = false, int timeout = 2000) => Options.Create(new CameraOptions
    {
        Adapter = "Rtsp",
        CaptureTimeoutMilliseconds = timeout,
        Rtsp = new()
        {
            TestMode = true,
            CameraAUrl = "rtsp://camera-a/stream",
            CameraBUrl = all ? "rtsp://camera-b/stream" : "",
            CameraCUrl = all ? "rtsp://camera-c/stream" : ""
        }
    });

    private sealed class Reader(Func<string, CancellationToken, Task<RtspDecodedFrame>> read) : IRtspFrameReader
    {
        public Task<RtspDecodedFrame> ReadAsync(string url, CancellationToken ct) => read(url, ct);
    }

    private sealed class StorageHandler : HttpMessageHandler
    {
        public int Writes { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Writes++;
            Assert.Equal("camera-service:rtsp-decoded-test", request.Headers.GetValues("X-Provenance").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { uri = "s3://test/frame.raw", sha256 = request.Headers.GetValues("X-Content-SHA256").Single() })
            });
        }
    }
}
