using System.ComponentModel.DataAnnotations;

namespace SmartMetrix.CameraService;

public sealed class CameraOptions
{
    public const string SectionName = "Camera";
    [Required] public string Adapter { get; init; } = "Arena";
    [Range(1, 60_000)] public double ExposureMicroseconds { get; init; } = 5_000;
    [Range(1, 120_000)] public int CaptureTimeoutMilliseconds { get; init; } = 10_000;
    [Range(0, long.MaxValue)] public long MaximumTimestampSkewNanoseconds { get; init; } = 1_000_000;
    [Required] public string TriggerSource { get; init; } = "Line0";
    [Required] public string TriggerActivation { get; init; } = "RisingEdge";
    [Required] public string PixelFormat { get; init; } = "Mono8";
    [Required] public string CameraASerialNumber { get; init; } = string.Empty;
    [Required] public string CameraBSerialNumber { get; init; } = string.Empty;
    [Required] public string CameraCSerialNumber { get; init; } = string.Empty;
    [Required] public string StorageServiceUrl { get; init; } = "http://localhost:5105";
    [Required] public string SimulatorFrameDirectory { get; init; } = "simulator-frames";
}

public sealed record CaptureRequest(string? CalibrationId = null);

public sealed record CapturedFrame(
    string CameraId,
    long FrameId,
    long HardwareTimestampNanoseconds,
    string ContentType,
    ReadOnlyMemory<byte> Payload);

public sealed record StoredFrame(
    string CameraId,
    long FrameId,
    long HardwareTimestampNanoseconds,
    string Uri,
    string Sha256);

public sealed record CaptureResponse(
    Guid MeasurementId,
    IReadOnlyList<StoredFrame> Frames,
    long TimestampSkewNanoseconds,
    string Adapter,
    string? CalibrationId);

public sealed class CameraCaptureException(string code, string message, int statusCode = 422) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
