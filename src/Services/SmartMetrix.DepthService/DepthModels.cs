using SmartMetrix.Contracts;

namespace SmartMetrix.DepthService;

public sealed record GrayFrame(string CameraId, int Width, int Height, byte[] Pixels);

public sealed record StereoPairCalibration(
    string LeftCameraId,
    string RightCameraId,
    double BaselineMetres,
    double Fx,
    double Fy,
    double Cx,
    double Cy,
    double[] Rotation,
    double[] Translation,
    string RectificationMapUri);

public sealed record DepthCalibrationBundle(
    int SchemaVersion,
    string CalibrationId,
    string CameraRigCoordinateSystemId,
    IReadOnlyList<StereoPairCalibration> Pairs);

public sealed record ReconstructionRequest(
    IReadOnlyList<GrayFrame> Frames,
    DepthCalibrationBundle Calibration);

public readonly record struct DisparitySample(float Disparity, float Confidence)
{
    public bool IsValid => float.IsFinite(Disparity) && Disparity > 0 && Confidence > 0;
    public static DisparitySample Invalid => new(float.NaN, 0);
}

public sealed record DisparityMap(int Width, int Height, DisparitySample[] Samples);
public readonly record struct Point3(float X, float Y, float Z, float Confidence);
public sealed record PointCloud(IReadOnlyList<Point3> Points, int InvalidPixelCount);

public sealed record ReconstructionResult(
    PointCloudCreated Event,
    int ValidPointCount,
    int InvalidPixelCount,
    IReadOnlyDictionary<string, int> SelectedBaselines);

public sealed class DepthOptions
{
    public const string SectionName = "Depth";
    public string Backend { get; set; } = "Cpu";
    public string NativeProvider { get; set; } = "OpenCvCuda";
    public int MinimumDisparity { get; set; } = 1;
    public int MaximumDisparity { get; set; } = 96;
    public int MatchRadius { get; set; } = 2;
    public float LeftRightTolerancePixels { get; set; } = 1.5f;
    public int MinimumSpeckleSize { get; set; } = 8;
    public float MinimumConfidence { get; set; } = 0.15f;
    public int UniquenessRatio { get; set; } = 10;
    public double NearDistanceMetres { get; set; } = 1.0;
    public double FarDistanceMetres { get; set; } = 20.0;
    public string StorageBaseUrl { get; set; } = "http://storage-service:8080";
}

public sealed class StereoBackendNotConfiguredException(string message) : InvalidOperationException(message);
