using Microsoft.Extensions.Options;
using SmartMetrix.Contracts;
using SmartMetrix.Messaging;
using SmartMetrix.TriggerService;

namespace SmartMetrix.ArchitectureTests;

public sealed class TriggerDecisionTests
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
    public async Task AcceptedDecisionEnqueuesCaptureRequestWithDiagnostics()
    {
        var engine = CreateEngine(out var clock);
        var outbox = new InMemoryOutboxStore();
        var coordinator = new TriggerCoordinator(engine, clock, outbox);
        await coordinator.EvaluateAsync(Safe, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));

        var result = await coordinator.EvaluateAsync(Safe, CancellationToken.None);

        Assert.NotNull(result.MeasurementId);
        var message = Assert.Single(await outbox.GetPendingAsync(10, CancellationToken.None));
        Assert.Equal(EventSubjects.For<CaptureRequested>(), message.Subject);
        var envelope = EventEnvelopeSerializer.Deserialize<CaptureRequested>(message.Payload);
        Assert.Equal("AutomaticConditionsSatisfied", envelope.Data.Reason);
        Assert.Equal(20, envelope.Data.Inputs?.DistanceMetres);
        Assert.Equal(result.MeasurementId, envelope.Data.MeasurementId.Value);
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
