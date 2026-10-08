using System.Text.Json.Serialization;

namespace SmartMetrix.CalibrationService;

public sealed record CameraIntrinsics(double Fx, double Fy, double Cx, double Cy, int Width, int Height);
public sealed record CameraCalibration(string CameraId, CameraIntrinsics Intrinsics, double[] Distortion, double[] Rotation, double[] Translation, string RectificationMapUri);
public sealed record RigPose(double[] Rotation, double[] Translation);
public sealed record RigGeometry(double AbMetres, double BcMetres, double AcMetres);

public sealed record CalibrationPayload(
    string RigId,
    IReadOnlyList<CameraCalibration> Cameras,
    RigGeometry Geometry,
    RigPose RigToPlatform,
    double ReprojectionErrorPixels,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SmartMetrix.Contracts.StereoRectification? Rectification = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SmartMetrix.Contracts.CalibrationAccuracy? Accuracy = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CalibrationStatus { Draft, Active, Revoked }

public sealed record CalibrationRecord(
    Guid Id,
    int Version,
    CalibrationPayload Payload,
    CalibrationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    string Checksum,
    string? RevocationReason);

public sealed record CalibrationBundle(int SchemaVersion, Guid CalibrationId, int Version, CalibrationPayload Calibration, string Checksum);
public sealed record CalibrationAuditEntry(Guid CalibrationId, string Action, DateTimeOffset At, string Actor, string? Details);
public sealed record ActivationRequest(DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, string Actor);
public sealed record RevocationRequest(string Actor, string Reason);

public sealed class CalibrationOptions
{
    public const string SectionName = "Calibration";
    public double MaximumReprojectionErrorPixels { get; set; } = 1.0;
    public double BaselineToleranceMetres { get; set; } = 0.02;
    public RigGeometry ExpectedGeometry { get; set; } = new(.7, .8, 1.5);
}
