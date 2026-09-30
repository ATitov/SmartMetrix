using System.Numerics;
using SmartMetrix.Domain;
using SmartMetrix.GeoreferenceService;

namespace SmartMetrix.ArchitectureTests;

public sealed class GeoreferenceTests
{
    private static readonly string[] ExpectedVersions = ["cal-4", "rig-7", "mount-2", "pose-11"];

    [Fact]
    [Trait("Requirement", "GEO-02")]
    public void ComposesCameraToQuarryAndPreservesEveryVersion()
    {
        var request = Request(new LocalPoint(1, 0, 0));
        var result = new Georeferencer().Georeference(Guid.NewGuid(), request);

        Assert.Equal("quarry:42", result.CoordinateSystemId);
        AssertPoint(result.Points[0].Position, 8, 2, 3);
        Assert.Equal(ExpectedVersions, result.Transforms.Select(x => x.Version));
        AssertPoint(result.Blocks[0].Centre, 8, 2, 3);
        AssertPoint(result.Blocks[0].Boundary[0], 8, 2, 3);
    }

    [Fact]
    public void RoundTripSyntheticTransformRestoresPoint()
    {
        var covariance = new double[36];
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3);
        var translation = new Vector3(4, -2, 1);
        var inverseRotation = Quaternion.Inverse(rotation);
        var inverseTranslation = Vector3.Transform(-translation, inverseRotation);
        var point = new LocalPoint(.5, 2, -1);
        var request = new GeoreferenceRequest(7, "camera", [point], [],
            [new("camera", "rig", "forward", new(translation, rotation), covariance),
             new("rig", "excavator", "inverse", new(inverseTranslation, inverseRotation), covariance)],
            new(7, "excavator", "quarry", "identity", Vector3.Zero, Quaternion.Identity, covariance));

        AssertPoint(new Georeferencer().Georeference(Guid.NewGuid(), request).Points[0].Position, .5, 2, -1);
    }

    [Fact]
    [Trait("Requirement", "GEO-03")]
    public void PositionAndAngleErrorsIncreaseUncertainty()
    {
        var request = Request(new LocalPoint(10, 0, 0));
        request.StaticTransforms[0].Covariance[0] = .04;
        request.StaticTransforms[0].Covariance[35] = .01;

        var result = new Georeferencer().Georeference(Guid.NewGuid(), request);

        Assert.InRange(result.Points[0].PositionUncertaintyMetres, 1.019, 1.021);
    }

    [Fact]
    public void RejectsMixedCoordinateSystemsAndMismatchedTimestamp()
    {
        var request = Request(new LocalPoint(0, 0, 0));
        var mixed = request with { PointCloudCoordinateSystemId = "another-camera" };
        Assert.Throws<CoordinateSystemMismatchException>(() => new Georeferencer().Georeference(Guid.NewGuid(), mixed));
        var stale = request with { PointCloudHardwareTimestampNanoseconds = 43 };
        Assert.Throws<ArgumentException>(() => new Georeferencer().Georeference(Guid.NewGuid(), stale));
    }

    [Fact]
    public void ResultCannotBeCreatedWithoutCoordinateSystem()
    {
        Assert.Throws<ArgumentException>(() => new GeoreferenceResult(Guid.NewGuid(), "", 1, [], [], []));
    }

    [Theory]
    [Trait("Requirement", "GEO-01")]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteGeometryIsRejected(double invalid)
    {
        Assert.Throws<ArgumentException>(() => new Georeferencer().Georeference(Guid.NewGuid(),
            Request(new LocalPoint(invalid, 0, 0))));
    }

    [Theory]
    [Trait("Requirement", "GEO-01")]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidCovarianceIsRejected(double invalid)
    {
        var request = Request(new LocalPoint(1, 0, 0));
        request.ExposurePose.Covariance[0] = invalid;
        Assert.Throws<ArgumentException>(() => new Georeferencer().Georeference(Guid.NewGuid(), request));
    }

    private static GeoreferenceRequest Request(LocalPoint point)
    {
        var zero = new double[36];
        var steps = new VersionedTransform[]
        {
            new("camera:left", "rig", "cal-4", new(new(1, 0, 0), Quaternion.Identity), (double[])zero.Clone()),
            new("rig", "platform", "rig-7", new(new(0, 2, 0), Quaternion.Identity), (double[])zero.Clone()),
            new("platform", "excavator", "mount-2", new(Vector3.Zero, Quaternion.Identity), (double[])zero.Clone())
        };
        var pose = new ExposurePose(42, "excavator", "quarry:42", "pose-11", new(10, 0, 3),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), (double[])zero.Clone());
        return new(42, "camera:left", [point], [new(Guid.NewGuid(), point, [point], 100, .9)], steps, pose);
    }

    private static void AssertPoint(LocalPoint actual, double x, double y, double z)
    {
        Assert.InRange(actual.XMetres, x - .001, x + .001);
        Assert.InRange(actual.YMetres, y - .001, y + .001);
        Assert.InRange(actual.ZMetres, z - .001, z + .001);
    }
}
