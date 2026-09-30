using Microsoft.Extensions.Options;
using NATS.Client.Core;
using SmartMetrix.Contracts;
using SmartMetrix.Domain;
using SmartMetrix.Messaging;

namespace SmartMetrix.TriggerService;

public sealed class TriggerPublicationException(Guid eventId, Guid measurementId, DateTimeOffset expiresAt)
    : Exception("Capture request publication was not confirmed within its validity window. Delivery may have occurred.")
{
    public Guid EventId { get; } = eventId;
    public Guid MeasurementId { get; } = measurementId;
    public DateTimeOffset ExpiresAt { get; } = expiresAt;
}

public sealed class TriggerCoordinator(TriggerDecisionEngine engine, TimeProvider clock, IEventPublisher publisher,
    IOptions<TriggerOptions> configured)
{
    public async Task<TriggerEvaluationResult> EvaluateAsync(TriggerSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decision = engine.Evaluate(snapshot);
        if (!decision.Accepted) return new(decision, null);

        var options = configured.Value;
        var measurementId = MeasurementId.New();
        var expiresAt = decision.EvaluatedAt.AddMilliseconds(options.RequestLifetimeMilliseconds);
        var inputs = new CaptureDecisionInputs(snapshot.CanSpeedMetresPerSecond, snapshot.EncoderSpeedMetresPerSecond,
            snapshot.VibrationRmsMetresPerSecondSquared, snapshot.AngularVelocityDegreesPerSecond,
            snapshot.DistanceMetres, snapshot.CamerasReady, snapshot.ManualCommand, snapshot.ManualInhibit);
        var request = new CaptureRequested(measurementId, decision.Reason, decision.EvaluatedAt, inputs, expiresAt);
        var envelope = new EventEnvelope<CaptureRequested>(Guid.NewGuid(), EventEnvelope.CurrentSchemaVersion,
            decision.EvaluatedAt, measurementId.ToString(), request);
        // Construct once: every attempt uses the identical id, payload and original deadline.
        var message = new OutboxMessage(envelope.EventId, EventSubjects.For<CaptureRequested>(),
            EventEnvelopeSerializer.Serialize(envelope), envelope.OccurredAt);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = expiresAt - clock.GetUtcNow();
        if (remaining > TimeSpan.Zero) deadline.CancelAfter(remaining);
        for (var attempt = 0; attempt < options.PublishAttempts && remaining > TimeSpan.Zero; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) break;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(options.PublishTimeoutMilliseconds, remaining.TotalMilliseconds)));
            try
            {
                await publisher.PublishAsync(message, timeout.Token);
                cancellationToken.ThrowIfCancellationRequested();
                if (!deadline.IsCancellationRequested && clock.GetUtcNow() < expiresAt)
                    return new(decision, measurementId.Value, envelope.EventId, expiresAt);
                break;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                error is NatsException or IOException or TimeoutException or OperationCanceledException)
            {
                // The broker may have persisted the request before its ACK was lost.
                // Keep the original id for JetStream deduplication; never persist a replay queue.
            }
            remaining = expiresAt - clock.GetUtcNow();
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new TriggerPublicationException(envelope.EventId, measurementId.Value, expiresAt);
    }
}
