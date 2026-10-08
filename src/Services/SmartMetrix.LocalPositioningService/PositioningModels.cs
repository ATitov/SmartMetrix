using System.Numerics;

namespace SmartMetrix.LocalPositioningService;

public enum PositionSourceType { TotalStation, Imu, Encoder, Gnss }

public sealed record PositioningSample(
    PositionSourceType SourceType,
    string SourceId,
    long HardwareTimestampNanoseconds,
    Vector3? PositionMetres,
    Quaternion? Orientation,
    double[] Covariance, string? ClockId = null, string? ProtocolVersion = null);

public sealed record RigidTransform(Vector3 TranslationMetres, Quaternion Rotation);

public sealed record TransformDefinition(
    string ExcavatorId,
    string CoordinateSystemId,
    int Version,
    RigidTransform ExcavatorToQuarry,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo = null);

public sealed record PoseRequest(
    string ExcavatorId,
    long HardwareTimestampNanoseconds,
    DateTimeOffset ExposedAt,
    IReadOnlyList<PositioningSample> Samples);

public sealed record PoseProvenance(
    PositionSourceType SourceType,
    string SourceId,
    long BeforeTimestampNanoseconds,
    long AfterTimestampNanoseconds, string? ClockId = null, string? ProtocolVersion = null);

public sealed record LocalizedPose(
    string CoordinateSystemId,
    int TransformVersion,
    long HardwareTimestampNanoseconds,
    Vector3 PositionMetres,
    Quaternion Orientation,
    double[] Covariance,
    string CovarianceFormat,
    IReadOnlyList<PoseProvenance> Sources);

public sealed class PositioningOptions
{
    public const string SectionName = "Positioning";
    public long MaximumSampleAgeNanoseconds { get; set; } = 250_000_000;
    public long MaximumInterpolationSpanNanoseconds { get; set; } = 1_000_000_000;
    public double MaximumSourceDisagreementMetres { get; set; } = 2.0;
}

public sealed class PoseUnavailableException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
