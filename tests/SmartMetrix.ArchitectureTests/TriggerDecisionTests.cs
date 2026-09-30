using Microsoft.Extensions.Options;
using SmartMetrix.Contracts;
using SmartMetrix.Messaging;
using SmartMetrix.TriggerService;

namespace SmartMetrix.ArchitectureTests;

public sealed partial class TriggerDecisionTests
{
    private static readonly TriggerSnapshot Safe = new(0, 0, 0.05, 0.1, 20, true, true, false, false);

    [Theory]
    [InlineData(0.2, 0, 0.05, 0.1, 20, true, false, "CanMovementDetected")]
    [InlineData(0, 0.2, 0.05, 0.1, 20, true, false, "EncoderMovementDetected")]
    [InlineData(0, 0, 0.2, 0.1, 20, true, false, "VibrationTooHigh")]
    [InlineData(0, 0, 0.05, 1, 20, true, false, "VibrationTooHigh")]
    [InlineData(0, 0, 0.05, 0.1, 10, true, false, "DistanceOutOfRange")]
    [InlineData(0, 0, 0.05, 0.1, 20, false, false, "CamerasNotReady")]
    [InlineData(0, 0, 0.05, 0.1, 20, true, true, "ManualInhibitActive")]
    public void UnsafeInputIsRejected(double can, double encoder, double vibration, double angular,
        double distance, bool camerasReady, bool inhibit, string reason)
    {
        var engine = CreateEngine(out _);
        var decision = engine.Evaluate(new(can, encoder, vibration, angular, distance, camerasReady, true, false, inhibit));
        Assert.False(decision.Accepted);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(can, decision.Inputs.CanSpeedMetresPerSecond);
    }

    [Fact]
    [Trait("Requirement", "TRG-02")]
    public void StableSafeInputIsAcceptedAfterDebounce()
    {
        var engine = CreateEngine(out var clock);
        Assert.Equal("Debouncing", engine.Evaluate(Safe).Reason);
        clock.Advance(TimeSpan.FromSeconds(1));
        var decision = engine.Evaluate(Safe);
        Assert.True(decision.Accepted);
        Assert.Equal("AutomaticConditionsSatisfied", decision.Reason);
    }

    [Fact]
    public void CooldownSuppressesRepeatedCapture()
    {
        var engine = CreateEngine(out var clock);
        engine.Evaluate(Safe);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(engine.Evaluate(Safe).Accepted);
        Assert.Equal("CooldownActive", engine.Evaluate(Safe).Reason);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("Debouncing", engine.Evaluate(Safe).Reason);
    }

    [Fact]
    public void UnsafeSampleResetsDebounce()
    {
        var engine = CreateEngine(out var clock);
        engine.Evaluate(Safe);
        clock.Advance(TimeSpan.FromMilliseconds(900));
        engine.Evaluate(Safe with { VibrationRmsMetresPerSecondSquared = 1 });
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal("Debouncing", engine.Evaluate(Safe).Reason);
    }

    [Fact]
    public void DistanceRangeConfigurationIsValidated()
    {
        var result = new TriggerOptionsValidator().Validate(null, new() { MinimumDistanceMetres = 30, MaximumDistanceMetres = 12 });
        Assert.True(result.Failed);
    }

    [Fact]
    [Trait("Requirement", "TRG-03")]
    public async Task AcceptedDecisionPublishesCaptureRequestWithDiagnostics()
    {
        var engine = CreateEngine(out var clock);
        var publisher = new RecordingPublisher();
        var coordinator = new TriggerCoordinator(engine, clock, publisher, Options.Create(new TriggerOptions()));
        await coordinator.EvaluateAsync(Safe, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));

        var result = await coordinator.EvaluateAsync(Safe, CancellationToken.None);

        Assert.NotNull(result.MeasurementId);
        var message = Assert.Single(publisher.Messages);
        Assert.Equal(EventSubjects.For<CaptureRequested>(), message.Subject);
        var envelope = EventEnvelopeSerializer.Deserialize<CaptureRequested>(message.Payload);
        Assert.Equal("AutomaticConditionsSatisfied", envelope.Data.Reason);
        Assert.Equal(20, envelope.Data.Inputs?.DistanceMetres);
        Assert.Equal(result.MeasurementId, envelope.Data.MeasurementId.Value);
    }

    [Theory]
    [Trait("Requirement", "TRG-01")]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void EveryNonFiniteSensorIsRejectedEvenForManualCapture(double value)
    {
        var input = Safe with { ManualCommand = true };
        TriggerSnapshot[] cases = [
            input with { CanSpeedMetresPerSecond = value },
            input with { EncoderSpeedMetresPerSecond = value },
            input with { VibrationRmsMetresPerSecondSquared = value },
            input with { AngularVelocityDegreesPerSecond = value },
            input with { DistanceMetres = value }];
        foreach (var sample in cases)
        {
            var decision = CreateEngine(out _).Evaluate(sample);
            Assert.False(decision.Accepted);
            Assert.Equal("InvalidSensorReading", decision.Reason);
        }
    }

    [Theory]
    [Trait("Requirement", "TRG-01")]
    [InlineData(12)]
    [InlineData(30)]
    public void InclusiveSafetyLimitsAllowCaptureAfterDebounce(double distance)
    {
        var engine = CreateEngine(out var clock);
        var input = Safe with
        {
            DistanceMetres = distance,
            CanSpeedMetresPerSecond = -.05,
            EncoderSpeedMetresPerSecond = .05,
            VibrationRmsMetresPerSecondSquared = .15,
            AngularVelocityDegreesPerSecond = -.5
        };
        Assert.Equal("Debouncing", engine.Evaluate(input).Reason);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(engine.Evaluate(input).Accepted);
    }

    private static TriggerDecisionEngine CreateEngine(out ManualTimeProvider clock)
    {
        clock = new(new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        return new(clock, Options.Create(new TriggerOptions()));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }
}
