using System.Numerics;
using Microsoft.Extensions.Options;
using SmartMetrix.LocalPositioningService;

namespace SmartMetrix.ArchitectureTests;

public sealed class LocalPositioningTests
{
    [Fact]
    [Trait("Requirement", "POS-02")]
    public void InterpolatesAtFrameHardwareTimestampAndComposesTransform()
    {
        var registry = new TransformRegistry();
        registry.Add(new("EX-1", "quarry:42", 7,
            new(new(10, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)), DateTimeOffset.UnixEpoch));
        var resolver = new PoseResolver(registry, Options.Create(new PositioningOptions { MaximumSampleAgeNanoseconds = 2_000_000_000 }));
        var covariance = Enumerable.Repeat(.1, 36).ToArray();
        var samples = new[]
        {
            new PositioningSample(PositionSourceType.TotalStation, "ts-1", 0, new(0, 0, 0), null, covariance),
            new PositioningSample(PositionSourceType.TotalStation, "ts-1", 1_000_000_000, new(2, 0, 0), null, covariance),
            new PositioningSample(PositionSourceType.Imu, "imu-1", 0, null, Quaternion.Identity, covariance),
            new PositioningSample(PositionSourceType.Imu, "imu-1", 1_000_000_000, null, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), covariance)
        };

        var pose = resolver.Resolve(new("EX-1", 500_000_000, DateTimeOffset.UnixEpoch.AddSeconds(1), samples));

        Assert.Equal("quarry:42", pose.CoordinateSystemId);
        Assert.Equal(7, pose.TransformVersion);
        Assert.InRange(pose.PositionMetres.X, 9.999f, 10.001f);
        Assert.InRange(pose.PositionMetres.Y, .999f, 1.001f);
        var forward = Vector3.Transform(Vector3.UnitX, pose.Orientation);
        Assert.InRange(forward.X, -.708f, -.706f);
        Assert.InRange(forward.Y, .706f, .708f);
        Assert.Equal(36, pose.Covariance.Length);
        Assert.Contains("6x6", pose.CovarianceFormat);
        Assert.Equal(2, pose.Sources.Count);
    }

    [Fact]
    [Trait("Requirement", "POS-01")]
    public void RejectsPoseWhenTimestampIsNotBracketed()
    {
        var registry = new TransformRegistry();
        registry.Add(new("EX-1", "quarry:42", 1, new(Vector3.Zero, Quaternion.Identity), DateTimeOffset.UnixEpoch));
        var resolver = new PoseResolver(registry, Options.Create(new PositioningOptions()));
        var covariance = new double[36];
        var samples = new[] { new PositioningSample(PositionSourceType.Gnss, "gnss", 0, Vector3.Zero, Quaternion.Identity, covariance) };

        var error = Assert.Throws<PoseUnavailableException>(() => resolver.Resolve(new("EX-1", 1, DateTimeOffset.UnixEpoch, samples)));
        Assert.Equal("StalePose", error.Code);
    }

    [Fact]
    [Trait("Requirement", "POS-01")]
    public void RejectsInconsistentIndependentPositionSources()
    {
        var registry = new TransformRegistry();
        registry.Add(new("EX-1", "quarry:42", 1, new(Vector3.Zero, Quaternion.Identity), DateTimeOffset.UnixEpoch));
        var resolver = new PoseResolver(registry, Options.Create(new PositioningOptions { MaximumSourceDisagreementMetres = 1 }));
        var covariance = new double[36];
        var samples = new[]
        {
            new PositioningSample(PositionSourceType.TotalStation, "ts", 1, Vector3.Zero, Quaternion.Identity, covariance),
            new PositioningSample(PositionSourceType.Gnss, "gnss", 1, new(5, 0, 0), Quaternion.Identity, covariance)
        };

        var error = Assert.Throws<PoseUnavailableException>(() => resolver.Resolve(new("EX-1", 1, DateTimeOffset.UnixEpoch, samples)));
        Assert.Equal("InconsistentPose", error.Code);
    }
}
