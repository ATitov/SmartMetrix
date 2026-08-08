using System.Numerics;

namespace SmartMetrix.Domain;

public sealed record CoordinateSystem(
    string Id,
    string Name,
    string Units,
    string OriginDescription,
    string VerticalDatum,
    int Version);

public readonly record struct LocalPoint(double XMetres, double YMetres, double ZMetres);

public sealed record LocalPose(
    string CoordinateSystemId,
    DateTimeOffset Timestamp,
    LocalPoint Position,
    Quaternion Orientation,
    IReadOnlyList<double> Covariance,
    string Source,
    string TransformVersion);

public sealed record LocalizedBlock(
    Guid BlockId,
    string CoordinateSystemId,
    LocalPoint Centre,
    IReadOnlyList<LocalPoint> Boundary,
    double PositionUncertaintyMetres,
    double EquivalentDiameterMillimetres,
    double Confidence);
