using System.Numerics;
using SmartMetrix.Contracts;
using SmartMetrix.Domain;
using SmartMetrix.Messaging;

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

    [Fact]
    public void EnvelopeRoundTripsWithoutLosingDeliveryMetadata()
    {
        var envelope = EventEnvelope.Create(
            new CaptureRequested(MeasurementId.New(), "operator", DateTimeOffset.UtcNow),
            "correlation-42");

        var restored = EventEnvelopeSerializer.Deserialize<CaptureRequested>(EventEnvelopeSerializer.Serialize(envelope));

        Assert.Equal(envelope, restored);
        Assert.Equal("smartmetrix.v1.capture-requested", EventSubjects.For<CaptureRequested>());
    }

    [Fact]
    public void UnsupportedSchemaVersionIsExplicitlyRejected()
    {
        var envelope = new EventEnvelope<CaptureRequested>(
            Guid.NewGuid(),
            2,
            DateTimeOffset.UtcNow,
            "correlation-42",
            new CaptureRequested(MeasurementId.New(), "operator", DateTimeOffset.UtcNow));

        Assert.Throws<UnsupportedSchemaVersionException>(() => EventEnvelopeSerializer.Serialize(envelope));
    }

    [Fact]
    public void OversizedPayloadMustBeMovedToObjectStorage()
    {
        var envelope = EventEnvelope.Create(
            new CaptureRequested(MeasurementId.New(), new string('x', EventEnvelopeSerializer.MaximumPayloadBytes), DateTimeOffset.UtcNow),
            "correlation-42");

        Assert.Throws<InvalidOperationException>(() => EventEnvelopeSerializer.Serialize(envelope));
    }

    [Fact]
    public async Task InboxPreventsDuplicateHandling()
    {
        var handled = 0;
        var handler = new IdempotentEventProcessor<CaptureRequested>(
            new InMemoryInboxStore(),
            (_, _) => { handled++; return Task.CompletedTask; });
        var envelope = EventEnvelope.Create(
            new CaptureRequested(MeasurementId.New(), "operator", DateTimeOffset.UtcNow, ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(2)),
            "correlation-42");

        Assert.True(await handler.HandleAsync(envelope));
        Assert.False(await handler.HandleAsync(envelope));
        Assert.Equal(1, handled);
    }

    [Fact]
    public void DurableConsumerHasExplicitRetryAndBackoffPolicy()
    {
        var options = new NatsMessagingOptions { MaximumDeliveries = 5, AckWaitSeconds = 20 };

        var config = JetStreamConsumerConfig.Durable<CaptureRequested>("camera-capture", options);

        Assert.Equal("camera-capture", config.DurableName);
        Assert.Equal(5, config.MaxDeliver);
        Assert.Equal(5, config.Backoff?.Count);
        Assert.Equal(EventSubjects.For<CaptureRequested>(), config.FilterSubject);
        Assert.Equal(EventSubjects.Prefix + ".dead-letter", EventSubjects.DeadLetter);
    }
}
