namespace SmartMetrix.QualityService;

public sealed record QualityFrame(
    string CameraId,
    int Width,
    int Height,
    byte[] GrayscalePixels,
    long HardwareTimestampNanoseconds);

public sealed record QualityRequest(string SceneType, IReadOnlyList<QualityFrame> Frames);

public sealed record FrameQualityMetrics(
    string CameraId,
    double Sharpness,
    double Exposure,
    double Saturation,
    double Texture,
    double ShadowFraction,
    double LensContamination);

public sealed record QualityResult(
    Guid MeasurementId,
    bool Accepted,
    string SceneType,
    string ThresholdVersion,
    IReadOnlyList<FrameQualityMetrics> Frames,
    IReadOnlyList<string> ReasonCodes,
    DateTimeOffset AssessedAt);

public static class QualityReasonCodes
{
    public const string MissingFrame = "missing_frame";
    public const string InvalidFrame = "invalid_frame";
    public const string InconsistentFrameSet = "inconsistent_frame_set";
    public const string FramesNotSynchronized = "frames_not_synchronized";
    public const string LowSharpness = "low_sharpness";
    public const string BadExposure = "bad_exposure";
    public const string HighSaturation = "high_saturation";
    public const string LowTexture = "low_texture";
    public const string ExcessiveShadow = "excessive_shadow";
    public const string LensContamination = "lens_contamination";
    public const string UnknownSceneType = "unknown_scene_type";
}
