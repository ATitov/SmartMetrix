using System.Numerics;
using Microsoft.Extensions.Options;

namespace SmartMetrix.LocalPositioningService;

public sealed class PoseResolver(TransformRegistry transforms, IOptions<PositioningOptions> configured)
{
    public const string CovarianceFormat = "row-major 6x6 [x,y,z,roll,pitch,yaw]; m^2, rad^2 and cross terms";
    private readonly PositioningOptions options = configured.Value;

    public LocalizedPose Resolve(PoseRequest request)
    {
        if (request.HardwareTimestampNanoseconds < 0) throw new ArgumentException("Hardware timestamp must be non-negative.");
        var groups = request.Samples.GroupBy(x => (x.SourceType, x.SourceId)).ToArray();
        if (request.Samples.Select(x => x.ClockId).Distinct().Count() > 1)
            throw new PoseUnavailableException("ClockMismatch", "All positioning samples must use the exposure hardware clock.");
        if (groups.Length == 0) throw new PoseUnavailableException("NoSamples", "No positioning samples were supplied.");

        var estimates = groups.Select(group => Interpolate(group.OrderBy(x => x.HardwareTimestampNanoseconds).ToArray(), request.HardwareTimestampNanoseconds)).ToArray();
        var positions = estimates.Where(x => x.Position is not null).ToArray();
        var rotations = estimates.Where(x => x.Orientation is not null).ToArray();
        if (positions.Length == 0 || rotations.Length == 0)
            throw new PoseUnavailableException("IncompletePose", "At least one position and orientation source are required.");

        for (var i = 0; i < positions.Length; i++)
            for (var j = i + 1; j < positions.Length; j++)
                if (Vector3.Distance(positions[i].Position!.Value, positions[j].Position!.Value) > options.MaximumSourceDisagreementMetres)
                    throw new PoseUnavailableException("InconsistentPose", "Position sources disagree beyond the configured threshold.");

        var localPosition = positions.Aggregate(Vector3.Zero, (sum, x) => sum + x.Position!.Value) / positions.Length;
        var localRotation = AverageRotations(rotations.Select(x => x.Orientation!.Value));
        var transform = transforms.Get(request.ExcavatorId, request.ExposedAt);
        var transformRotation = Quaternion.Normalize(transform.ExcavatorToQuarry.Rotation);
        var quarryPosition = Vector3.Transform(localPosition, transformRotation) + transform.ExcavatorToQuarry.TranslationMetres;
        var quarryRotation = Quaternion.Normalize(transformRotation * localRotation);
        var covariance = AverageCovariance(estimates);
        return new(transform.CoordinateSystemId, transform.Version, request.HardwareTimestampNanoseconds, quarryPosition,
            quarryRotation, covariance, CovarianceFormat, estimates.Select(x => x.Provenance).ToArray());
    }

    private Estimate Interpolate(PositioningSample[] samples, long target)
    {
        var before = samples.LastOrDefault(x => x.HardwareTimestampNanoseconds <= target);
        var after = samples.FirstOrDefault(x => x.HardwareTimestampNanoseconds >= target);
        if (before is null || after is null)
            throw new PoseUnavailableException("StalePose", "The target timestamp is not bracketed by positioning samples.");
        var age = Math.Max(target - before.HardwareTimestampNanoseconds, after.HardwareTimestampNanoseconds - target);
        var span = after.HardwareTimestampNanoseconds - before.HardwareTimestampNanoseconds;
        if (age > options.MaximumSampleAgeNanoseconds || span > options.MaximumInterpolationSpanNanoseconds)
            throw new PoseUnavailableException("StalePose", "Positioning samples are too old or too far apart.");
        ValidateCovariance(before.Covariance); ValidateCovariance(after.Covariance);
        var t = span == 0 ? 0f : (float)(target - before.HardwareTimestampNanoseconds) / span;
        Vector3? position = before.PositionMetres is { } p0 && after.PositionMetres is { } p1 ? Vector3.Lerp(p0, p1, t) : before.PositionMetres ?? after.PositionMetres;
        Quaternion? orientation = before.Orientation is { } q0 && after.Orientation is { } q1 ? Quaternion.Normalize(Quaternion.Slerp(q0, q1, t)) : before.Orientation ?? after.Orientation;
        var covariance = before.Covariance.Zip(after.Covariance, (a, b) => a + (b - a) * t).ToArray();
        return new(position, orientation, covariance, new(before.SourceType, before.SourceId, before.HardwareTimestampNanoseconds, after.HardwareTimestampNanoseconds,
            before.ClockId, before.ProtocolVersion));
    }

    private static Quaternion AverageRotations(IEnumerable<Quaternion> values)
    {
        var rotations = values.Select(Quaternion.Normalize).ToArray();
        var reference = rotations[0];
        var sum = Vector4.Zero;
        foreach (var q in rotations)
        {
            var v = new Vector4(q.X, q.Y, q.Z, q.W);
            if (Quaternion.Dot(reference, q) < 0) v = -v;
            sum += v;
        }
        return Quaternion.Normalize(new(sum.X, sum.Y, sum.Z, sum.W));
    }

    private static double[] AverageCovariance(Estimate[] estimates)
    {
        var result = new double[36];
        foreach (var estimate in estimates)
            for (var i = 0; i < result.Length; i++) result[i] += estimate.Covariance[i] / estimates.Length;
        return result;
    }

    private static void ValidateCovariance(double[] value)
    {
        if (value.Length != 36 || value.Any(x => !double.IsFinite(x)))
            throw new ArgumentException("Covariance must contain 36 finite values.");
    }

    private sealed record Estimate(Vector3? Position, Quaternion? Orientation, double[] Covariance, PoseProvenance Provenance);
}
