using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace SmartMetrix.CameraService;

public sealed class RtspCameraOptions
{
    public bool TestMode { get; init; }
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string CameraAUrl { get; init; } = "";
    public string CameraBUrl { get; init; } = "";
    public string CameraCUrl { get; init; } = "";
    public int MaximumPixels { get; init; } = 12_000_000;
}

public sealed record RtspDecodedFrame(int Width, int Height, byte[] Pixels);

public interface IRtspFrameReader
{
    Task<RtspDecodedFrame> ReadAsync(string url, CancellationToken ct);
}

public sealed class RtspCameraAdapter(IOptions<CameraOptions> configured, IRtspFrameReader reader) : ICameraAdapter, IDisposable
{
    private readonly CameraOptions options = configured.Value;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long sequence;
    public string Name => "Rtsp";

    public Task<IReadOnlyList<CapturedFrame>> CaptureAsync(CancellationToken cancellationToken) =>
        CaptureSetAsync(["A", "B", "C"], cancellationToken);

    public async Task<CapturedFrame> CaptureCameraAsync(string cameraId, CancellationToken cancellationToken) =>
        (await CaptureSetAsync([cameraId.ToUpperInvariant()], cancellationToken))[0];

    private async Task<IReadOnlyList<CapturedFrame>> CaptureSetAsync(string[] ids, CancellationToken ct)
    {
        if (!options.Rtsp.TestMode || options.PixelFormat != "Mono8")
            throw new CameraCaptureException("NotConfigured", "RTSP capture requires Camera:Rtsp:TestMode=true and Mono8.", 503);
        // Validate all addresses before opening any connection. Never echo credentials in errors.
        var urls = ids.Select(GetUrl).ToArray();
        await gate.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.CaptureTimeoutMilliseconds);
            var frameId = Interlocked.Increment(ref sequence);
            async Task<CapturedFrame> CaptureOne(int index)
            {
                try
                {
                    var image = await reader.ReadAsync(urls[index], timeout.Token);
                    if (image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height > options.Rtsp.MaximumPixels ||
                        (long)image.Width * image.Height != image.Pixels.Length)
                        throw new CameraCaptureException("InvalidRtspFrame", "Decoded RTSP frame has invalid dimensions or size.", 502);
                    return new CapturedFrame(ids[index], frameId, 0, "application/octet-stream", image.Pixels,
                        DateTimeOffset.UtcNow, image.Width, image.Height, "HostReceive");
                }
                catch
                {
                    await timeout.CancelAsync();
                    throw;
                }
            }
            try
            {
                var frames = await Task.WhenAll(Enumerable.Range(0, ids.Length).Select(CaptureOne));
                if (frames.Select(frame => (frame.Width, frame.Height)).Distinct().Count() != 1)
                    throw new CameraCaptureException("FrameSizeMismatch", "RTSP cameras must use the same image dimensions.");
                return frames;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new CameraCaptureException("CaptureTimeout", "RTSP capture exceeded its time limit.", 504);
            }
        }
        finally { gate.Release(); }
    }

    private string GetUrl(string id)
    {
        var url = id switch
        {
            "A" => options.Rtsp.CameraAUrl,
            "B" => options.Rtsp.CameraBUrl,
            "C" => options.Rtsp.CameraCUrl,
            _ => throw new CameraCaptureException("UnknownCamera", "Camera ID must be A, B or C.", 404)
        };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "rtsp" || string.IsNullOrWhiteSpace(uri.Host))
            throw new CameraCaptureException("NotConfigured", $"Configure a valid RTSP URL for camera {id}.", 503);
        return url;
    }

    public void Dispose() => gate.Dispose();
}

public sealed class FfmpegRtspFrameReader(IOptions<CameraOptions> configured) : IRtspFrameReader
{
    private readonly CameraOptions options = configured.Value;

    public async Task<RtspDecodedFrame> ReadAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(options.Rtsp.FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // ArgumentList avoids shell interpolation. Original dimensions are preserved.
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-rtsp_transport", "tcp",
            "-i", url, "-map", "0:v:0", "-frames:v", "1", "-an", "-sn", "-pix_fmt", "gray", "-c:v", "pgm", "-f", "image2pipe", "pipe:1" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new CameraCaptureException("NotConfigured", "FFmpeg could not be started. Configure Camera:Rtsp:FfmpegPath.", 503);
        }

        // FFmpeg errors may contain the URL/password. Drain without retaining or logging them.
        var errors = DrainAsync(process.StandardError);
        try
        {
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) != 0)
            {
                if (output.Length + count > (long)options.Rtsp.MaximumPixels + 1024)
                    throw new CameraCaptureException("InvalidRtspFrame", "Decoded RTSP frame exceeds the configured size limit.", 502);
                output.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                throw new CameraCaptureException("RtspCaptureFailed", "RTSP capture failed. Check camera connectivity, credentials and stream settings.", 502);
            return DecodePgm(output.ToArray(), options.Rtsp.MaximumPixels);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await errors;
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) != 0) { }
    }

    // FFmpeg's PGM encoder emits a P5 header followed by exactly width*height gray bytes.
    public static RtspDecodedFrame DecodePgm(byte[] bytes, int maximumPixels)
    {
        var offset = 0;
        string Token()
        {
            while (offset < bytes.Length && bytes[offset] is 9 or 10 or 13 or 32) offset++;
            var start = offset;
            while (offset < bytes.Length && bytes[offset] is not (9 or 10 or 13 or 32)) offset++;
            return Encoding.ASCII.GetString(bytes, start, offset - start);
        }
        if (Token() != "P5" || !int.TryParse(Token(), NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(Token(), NumberStyles.None, CultureInfo.InvariantCulture, out var height) || Token() != "255" ||
            width <= 0 || height <= 0 || (long)width * height > maximumPixels || offset >= bytes.Length)
            throw InvalidFrame();
        // Consume only the header separator: pixel values may themselves be whitespace.
        if (bytes[offset++] == 13 && offset < bytes.Length && bytes[offset] == 10) offset++;
        if (bytes.Length - offset != (long)width * height) throw InvalidFrame();
        return new RtspDecodedFrame(width, height, bytes[offset..]);
    }

    private static CameraCaptureException InvalidFrame() => new("InvalidRtspFrame", "FFmpeg returned an invalid Mono8 PGM frame.", 502);
}
