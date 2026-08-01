using System.Numerics;
using SmartMetrix.Contracts;
using SmartMetrix.Domain;

namespace SmartMetrix.ArchitectureTests;

public sealed class ContractTests
{
    [Fact]
    public void SpatialResultAlwaysIdentifiesCoordinateSystem()
    {
        var block = new LocalizedBlock(
            Guid.NewGuid(),
            "QUARRY-01-LOCAL-2026",
            new LocalPoint(1, 2, 3),
            [],
            0.05,
            450,
            0.93);

        Assert.False(string.IsNullOrWhiteSpace(block.CoordinateSystemId));
    }

    [Fact]
    public void PoseCarriesTransformVersionAndUncertainty()
    {
        var pose = new LocalPose(
            "QUARRY-01-LOCAL-2026",
            DateTimeOffset.UtcNow,
            new LocalPoint(1, 2, 3),
            Quaternion.Identity,
            new double[36],
            "TotalStation+IMU",
            "transform-3");

        Assert.Equal(36, pose.Covariance.Count);
        Assert.NotEmpty(pose.TransformVersion);
    }

    [Fact]
    public void LargePayloadContractsUseUrisInsteadOfByteArrays()
    {
        var messageTypes = typeof(CaptureRequested).Assembly.GetTypes();

        var binaryProperties = messageTypes
            .SelectMany(type => type.GetProperties())
            .Where(property => property.PropertyType == typeof(byte[]))
            .ToArray();

        Assert.Empty(binaryProperties);
    }
}
