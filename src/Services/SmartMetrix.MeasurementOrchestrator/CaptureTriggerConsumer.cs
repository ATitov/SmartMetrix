using System.Text.Json;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream.Models;
using NATS.Net;
using SmartMetrix.Contracts;
using SmartMetrix.Messaging;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed class CaptureTriggerOptions
{
    public const string SectionName = "CaptureTrigger";
    public bool Enabled { get; set; }
    public string RigId { get; set; } = "";
    public string ExcavatorId { get; set; } = "";
    public string CoordinateSystemId { get; set; } = "";
    public string ConsumerName { get; set; } = "";
    public bool IsValid() => !Enabled || new[] { RigId, ExcavatorId, CoordinateSystemId, ConsumerName }.All(x =>
        !string.IsNullOrWhiteSpace(x) && x.Length <= 128 && !x.Any(c => char.IsWhiteSpace(c) || c is '.' or '*' or '>' or '/' or '\\'));
}

public sealed class CaptureTriggerHandler(MeasurementWorkflow workflow, IOptions<CaptureTriggerOptions> configured,
    IOptions<PipelineOptions> pipeline, TimeProvider clock)
{
    public async Task<bool> HandleAsync(EventEnvelope<CaptureRequested> envelope, CancellationToken ct)
    {
        var options = configured.Value;
        var data = envelope.Data;
        if (!options.Enabled || envelope.EventId == Guid.Empty || data.MeasurementId.Value == Guid.Empty ||
            data.RigId != options.RigId || data.ExcavatorId != options.ExcavatorId ||
            data.ExpiresAt is null || data.ExpiresAt <= clock.GetUtcNow() || data.RequestedAt > clock.GetUtcNow()) return false;
        if (!pipeline.Value.Enabled || pipeline.Value.RigId != options.RigId)
            throw new InvalidOperationException("Trigger rig does not match the configured processing pipeline.");
        await workflow.StartAsync(envelope.EventId, data.MeasurementId.Value, options.ExcavatorId,
            options.CoordinateSystemId, data.Reason, ct);
        return true;
    }
}

public sealed class CaptureTriggerConsumer(NatsClient client, CaptureTriggerHandler handler,
    IOptions<CaptureTriggerOptions> configured, IOptions<NatsMessagingOptions> messaging,
    ILogger<CaptureTriggerConsumer> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogRetry = LoggerMessage.Define(LogLevel.Warning,
        new EventId(2101, "CaptureConsumerRetry"), "Capture request consumer will reconnect; unacknowledged requests remain in JetStream.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configured.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var context = client.CreateJetStreamContext();
                await context.CreateOrUpdateStreamAsync(new StreamConfig(messaging.Value.StreamName, [EventSubjects.All])
                {
                    Storage = StreamConfigStorage.File,
                    Retention = StreamConfigRetention.Limits,
                    MaxMsgSize = EventEnvelopeSerializer.MaximumPayloadBytes,
                    DuplicateWindow = TimeSpan.FromHours(2)
                }, stoppingToken);
                var consumer = await context.CreateOrUpdateConsumerAsync(messaging.Value.StreamName,
                    JetStreamConsumerConfig.Durable<CaptureRequested>(configured.Value.ConsumerName, messaging.Value), stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    var message = await consumer.NextAsync<byte[]>(cancellationToken: stoppingToken);
                    if (message is null) continue;
                    try
                    {
                        var envelope = EventEnvelopeSerializer.Deserialize<CaptureRequested>(message.Data!);
                        await handler.HandleAsync(envelope, stoppingToken);
                    }
                    catch (Exception error) when (error is JsonException or UnsupportedSchemaVersionException or ArgumentException)
                    {
                        // Invalid/unsupported envelopes cannot become valid by redelivery.
                    }
                    await message.AckAsync(cancellationToken: stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { LogRetry(logger, null); }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
