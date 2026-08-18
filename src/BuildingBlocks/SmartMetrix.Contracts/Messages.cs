using SmartMetrix.Domain;

namespace SmartMetrix.Contracts;

public sealed record CaptureRequested(
    MeasurementId MeasurementId,
    string Reason,
    DateTimeOffset RequestedAt,
    CaptureDecisionInputs? Inputs = null);

public sealed record CaptureDecisionInputs(
    double CanSpeedMetresPerSecond,
    double EncoderSpeedMetresPerSecond,
    double VibrationRmsMetresPerSecondSquared,
    double AngularVelocityDegreesPerSecond,
    double DistanceMetres,
    bool CamerasReady,
    bool ManualCommand,
    bool ManualInhibit);

public sealed record FrameReference(
    string CameraId,
    Uri Uri,
    DateTimeOffset ExposedAt,
    long HardwareTimestampNanoseconds);

public sealed record CaptureCompleted(
    MeasurementId MeasurementId,
    IReadOnlyList<FrameReference> Frames,
    string CalibrationId);

public sealed record QualityAssessed(
    MeasurementId MeasurementId,
    bool Accepted,
    double Sharpness,
    double Exposure,
    double LensContamination,
    double TextureScore,
    IReadOnlyList<string> RejectionReasons);

public sealed record ReconstructionRequested(
    MeasurementId MeasurementId,
    IReadOnlyList<FrameReference> Frames,
    string CalibrationId);

public sealed record PointCloudCreated(
    MeasurementId MeasurementId,
    Uri PointCloudUri,
    Uri ConfidenceMapUri,
    string CameraRigCoordinateSystemId);

public sealed record SegmentationCreated(
    MeasurementId MeasurementId,
    Uri MaskUri,
    string ModelVersion,
    double Confidence);

public sealed record MeasurementCompleted(
    MeasurementId MeasurementId,
    string CoordinateSystemId,
    IReadOnlyList<LocalizedBlock> Blocks,
    IReadOnlyDictionary<string, double> SizeDistribution,
    double D50Millimetres,
    double D80Millimetres,
    double Confidence);
