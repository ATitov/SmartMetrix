using System.Numerics;
using SmartMetrix.Domain;

namespace SmartMetrix.GeoreferenceService;

public sealed class Georeferencer
{
    private static readonly int[] CovarianceDiagonal = [0, 7, 14, 21, 28, 35];

    public GeoreferenceResult Georeference(Guid measurementId, GeoreferenceRequest request)
    {
        Validate(request);
        var chain = request.StaticTransforms.ToArray();
        var pose = request.ExposurePose;
        var provenance = chain.Select(x => new TransformProvenance(x.FromCoordinateSystemId, x.ToCoordinateSystemId, x.Version))
            .Append(new(pose.FromCoordinateSystemId, pose.CoordinateSystemId, pose.TransformVersion)).ToArray();

        var points = request.Points.Select(point =>
        {
            var transformed = Transform(point, chain, pose);
            return new GeoreferencedPoint(transformed, Uncertainty(point, chain, pose));
        }).ToArray();
        var blocks = request.Blocks.Select(block =>
        {
            var centre = Transform(block.Centre, chain, pose);
            var boundary = block.Boundary.Select(x => Transform(x, chain, pose)).ToArray();
            return new GeoreferencedBlock(block.BlockId, centre, boundary, Uncertainty(block.Centre, chain, pose),
                block.EquivalentDiameterMillimetres, block.Confidence);
        }).ToArray();
        return new(measurementId, pose.CoordinateSystemId, request.PointCloudHardwareTimestampNanoseconds,
            points, blocks, provenance);
    }

    private static LocalPoint Transform(LocalPoint point, IReadOnlyList<VersionedTransform> chain, ExposurePose pose)
    {
        var value = new Vector3((float)point.XMetres, (float)point.YMetres, (float)point.ZMetres);
        foreach (var step in chain)
            value = Vector3.Transform(value, Quaternion.Normalize(step.Transform.Rotation)) + step.Transform.TranslationMetres;
        value = Vector3.Transform(value, Quaternion.Normalize(pose.Orientation)) + pose.PositionMetres;
        return new(value.X, value.Y, value.Z);
    }

    private static double Uncertainty(LocalPoint point, IReadOnlyList<VersionedTransform> chain, ExposurePose pose)
    {
        var radius = Math.Sqrt(point.XMetres * point.XMetres + point.YMetres * point.YMetres + point.ZMetres * point.ZMetres);
        var variance = PositionalVariance(pose.Covariance) + AngularVariance(pose.Covariance) * radius * radius;
        foreach (var step in chain)
            variance += PositionalVariance(step.Covariance) + AngularVariance(step.Covariance) * radius * radius;
        return Math.Sqrt(Math.Max(0, variance));
    }

    private static double PositionalVariance(double[] covariance) => covariance[0] + covariance[7] + covariance[14];
    private static double AngularVariance(double[] covariance) => covariance[21] + covariance[28] + covariance[35];

    private static void Validate(GeoreferenceRequest request)
    {
        if (request.PointCloudHardwareTimestampNanoseconds < 0 ||
            request.PointCloudHardwareTimestampNanoseconds != request.ExposurePose.HardwareTimestampNanoseconds)
            throw new ArgumentException("Point cloud must be matched to the pose at its exposure timestamp.");
        if (string.IsNullOrWhiteSpace(request.PointCloudCoordinateSystemId) || string.IsNullOrWhiteSpace(request.ExposurePose.CoordinateSystemId))
            throw new ArgumentException("Source and target coordinate systems are required.");
        if (request.StaticTransforms.Count == 0) throw new ArgumentException("At least one static transform is required.");

        var expected = request.PointCloudCoordinateSystemId;
        foreach (var step in request.StaticTransforms)
        {
            if (!string.Equals(step.FromCoordinateSystemId, expected, StringComparison.Ordinal))
                throw new CoordinateSystemMismatchException(expected, step.FromCoordinateSystemId);
            ValidateStep(step.Version, step.Transform.Rotation, step.Covariance);
            expected = step.ToCoordinateSystemId;
        }
        if (!string.Equals(expected, request.ExposurePose.FromCoordinateSystemId, StringComparison.Ordinal))
            throw new CoordinateSystemMismatchException(expected, request.ExposurePose.FromCoordinateSystemId);
        ValidateStep(request.ExposurePose.TransformVersion, request.ExposurePose.Orientation, request.ExposurePose.Covariance);
        if (request.Points.Any(x => !Finite(x)) || request.Blocks.Any(x => !Finite(x.Centre) || x.Boundary.Any(p => !Finite(p))))
            throw new ArgumentException("All geometry coordinates must be finite.");
    }

    private static void ValidateStep(string version, Quaternion rotation, double[] covariance)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Every transform version is required.");
        if (!float.IsFinite(rotation.LengthSquared()) || Math.Abs(rotation.LengthSquared() - 1) > .001)
            throw new ArgumentException("Every transform rotation must be normalized.");
        if (covariance.Length != 36 || covariance.Any(x => !double.IsFinite(x)) ||
            CovarianceDiagonal.Any(i => covariance[i] < 0))
            throw new ArgumentException("Transform covariance must be a finite row-major 6x6 matrix with non-negative diagonal.");
    }

    private static bool Finite(LocalPoint p) => double.IsFinite(p.XMetres) && double.IsFinite(p.YMetres) && double.IsFinite(p.ZMetres);
}

public sealed class CoordinateSystemMismatchException(string expected, string actual)
    : Exception($"Incompatible coordinate systems: expected '{expected}', received '{actual}'.");
