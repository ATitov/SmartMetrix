using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

namespace SmartMetrix.CameraService;

public interface ICameraAdapter
{
    string Name { get; }
    Task<IReadOnlyList<CapturedFrame>> CaptureAsync(CancellationToken cancellationToken);
}

public sealed class ArenaCameraAdapter(IOptions<CameraOptions> configured) : ICameraAdapter, IDisposable
{
    private const string LibraryName = "smartmetrix_arena";
    private readonly CameraOptions options = configured.Value;
    private nint context;
    public string Name => "Arena";

    public Task<IReadOnlyList<CapturedFrame>> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            EnsureInitialized();
            var status = Native.Capture(context, out var nativeSet);
            if (status != ArenaStatus.Ok)
            {
                throw MapStatus(status);
            }

            try
            {
                var frames = new List<CapturedFrame>(nativeSet.Count);
                var size = Marshal.SizeOf<NativeFrame>();
                for (var index = 0; index < nativeSet.Count; index++)
                {
                    var item = Marshal.PtrToStructure<NativeFrame>(nativeSet.Frames + index * size);
                    var payload = new byte[checked((int)item.Size)];
                    Marshal.Copy(item.Data, payload, 0, payload.Length);
                    frames.Add(new CapturedFrame(
                        Marshal.PtrToStringUTF8(item.CameraId) ?? string.Empty,
                        item.FrameId,
                        item.HardwareTimestampNanoseconds,
                        Marshal.PtrToStringUTF8(item.ContentType) ?? "application/octet-stream",
                        payload));
                }
                return Task.FromResult<IReadOnlyList<CapturedFrame>>(frames);
            }
            finally
            {
                Native.ReleaseFrameSet(context, ref nativeSet);
            }
        }
        catch (DllNotFoundException)
        {
            throw new CameraCaptureException("NotConfigured", "Arena SDK adapter is not installed.", 503);
        }
        catch (EntryPointNotFoundException)
        {
            throw new CameraCaptureException("NotConfigured", "Arena adapter ABI is incompatible.", 503);
        }
    }

    private void EnsureInitialized()
    {
        if (context != 0) return;
        var configuration = new NativeConfiguration
        {
            ExposureMicroseconds = options.ExposureMicroseconds,
            RequiredCameraCount = 3
        };
        var status = Native.Create(ref configuration, out context);
        if (status != ArenaStatus.Ok) throw MapStatus(status);
    }

    private static CameraCaptureException MapStatus(ArenaStatus status) => status switch
    {
        ArenaStatus.NotConfigured => new("NotConfigured", "Arena SDK or the three-camera rig is not configured.", 503),
        ArenaStatus.FrameMissing => new("FrameSetIncomplete", "A triggered frame set lost one or more frames."),
        ArenaStatus.Timeout => new("CaptureTimeout", "Timed out waiting for the triggered frame set.", 504),
        _ => new("CameraFailure", $"Arena adapter failed with status {(int)status}.", 502)
    };

    public void Dispose()
    {
        if (context == 0) return;
        Native.Destroy(context);
        context = 0;
    }

    private enum ArenaStatus { Ok = 0, NotConfigured = 1, FrameMissing = 2, Timeout = 3 }
    [StructLayout(LayoutKind.Sequential)] private struct NativeConfiguration { public double ExposureMicroseconds; public int RequiredCameraCount; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeFrame { public nint CameraId; public long FrameId; public long HardwareTimestampNanoseconds; public nint Data; public nuint Size; public nint ContentType; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeFrameSet { public nint Frames; public int Count; }
    private static class Native
    {
        [DllImport(LibraryName, EntryPoint = "smartmetrix_arena_create")] internal static extern ArenaStatus Create(ref NativeConfiguration configuration, out nint context);
        [DllImport(LibraryName, EntryPoint = "smartmetrix_arena_capture")] internal static extern ArenaStatus Capture(nint context, out NativeFrameSet frameSet);
        [DllImport(LibraryName, EntryPoint = "smartmetrix_arena_release_frame_set")] internal static extern void ReleaseFrameSet(nint context, ref NativeFrameSet frameSet);
        [DllImport(LibraryName, EntryPoint = "smartmetrix_arena_destroy")] internal static extern void Destroy(nint context);
    }
}

public sealed class SimulatorCameraAdapter(IOptions<CameraOptions> configured, IHostEnvironment environment) : ICameraAdapter
{
    private readonly CameraOptions options = configured.Value;
    private long sequence;
    public string Name => "Simulator";

    public async Task<IReadOnlyList<CapturedFrame>> CaptureAsync(CancellationToken cancellationToken)
    {
        var directory = Path.IsPathRooted(options.SimulatorFrameDirectory)
            ? options.SimulatorFrameDirectory
            : Path.Combine(environment.ContentRootPath, options.SimulatorFrameDirectory);
        var frameId = Interlocked.Increment(ref sequence);
        var frames = new List<CapturedFrame>(3);
        for (var index = 0; index < 3; index++)
        {
            var cameraId = ((char)('A' + index)).ToString();
            var path = Path.Combine(directory, $"camera-{cameraId.ToLowerInvariant()}.raw");
            if (!File.Exists(path))
                throw new CameraCaptureException("NotConfigured", $"Simulator fixture is missing: {path}", 503);
            frames.Add(new CapturedFrame(cameraId, frameId, 1_000_000_000 + index * 100_000,
                "application/octet-stream", await File.ReadAllBytesAsync(path, cancellationToken)));
        }
        return frames;
    }
}
