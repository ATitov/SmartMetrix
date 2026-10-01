using System.Numerics;
using SmartMetrix.Domain;

namespace SmartMetrix.GeoreferenceService;

public sealed record RigidTransform3(Vector3 TranslationMetres, Quaternion Rotation);

public sealed record VersionedTransform(
    string FromCoordinateSystemId,
    string ToCoordinateSystemId,
    string Version,
    RigidTransform3 Transform,
    double[] Covariance);

public sealed record ExposurePose(
    long HardwareTimestampNanoseconds,
    string FromCoordinateSystemId,
    string CoordinateSystemId,
    string TransformVersion,
    Vector3 PositionMetres,
    Quaternion Orientation,
    double[] Covariance);

public sealed record CameraBlock(Guid BlockId, LocalPoint Centre, IReadOnlyList<LocalPoint> Boundary,
    double EquivalentDiameterMillimetres, double Confidence, string BoundaryKind = "unspecified");

public sealed record GeoreferenceRequest(
    long PointCloudHardwareTimestampNanoseconds,
    string PointCloudCoordinateSystemId,
    IReadOnlyList<LocalPoint> Points,
    IReadOnlyList<CameraBlock> Blocks,
    IReadOnlyList<VersionedTransform> StaticTransforms,
    ExposurePose ExposurePose);

public sealed record TransformProvenance(string FromCoordinateSystemId, string ToCoordinateSystemId, string Version);
public sealed record GeoreferencedPoint(LocalPoint Position, double PositionUncertaintyMetres);
public sealed record GeoreferencedBlock(Guid BlockId, LocalPoint Centre, IReadOnlyList<LocalPoint> Boundary,
    double PositionUncertaintyMetres, double EquivalentDiameterMillimetres, double Confidence, string BoundaryKind = "unspecified");

public sealed class GeoreferenceResult
{
    public GeoreferenceResult(Guid measurementId, string coordinateSystemId, long hardwareTimestampNanoseconds,
        IReadOnlyList<GeoreferencedPoint> points, IReadOnlyList<GeoreferencedBlock> blocks,
        IReadOnlyList<TransformProvenance> transforms)
    {
        if (string.IsNullOrWhiteSpace(coordinateSystemId))
            throw new ArgumentException("Coordinate system is required.", nameof(coordinateSystemId));
        MeasurementId = measurementId;
        CoordinateSystemId = coordinateSystemId;
        HardwareTimestampNanoseconds = hardwareTimestampNanoseconds;
        Points = points;
        Blocks = blocks;
        Transforms = transforms;
    }

    public Guid MeasurementId { get; }
    public string CoordinateSystemId { get; }
    public long HardwareTimestampNanoseconds { get; }
    public IReadOnlyList<GeoreferencedPoint> Points { get; }
    public IReadOnlyList<GeoreferencedBlock> Blocks { get; }
    public IReadOnlyList<TransformProvenance> Transforms { get; }
}
