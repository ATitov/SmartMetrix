using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Net;
using SmartMetrix.Contracts;
using SmartMetrix.MeasurementOrchestrator;
using SmartMetrix.Messaging;

namespace SmartMetrix.ArchitectureTests;

[Collection("NatsInfrastructure")]
public sealed class CaptureTriggerIntegrationTests
{
    [NatsFact]
    [Trait("Category", "Integration")]
    public async Task UnacknowledgedCaptureIsRedeliveredAndReplayAfterRestartDoesNotCreateAnotherMeasurement()
    {
        await using var server = await NatsTestServer.CreateAsync();
        await using var client = new NatsClient(server.Url);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var messaging = Options.Create(new NatsMessagingOptions { Url = server.Url, StreamName = server.StreamName, AckWaitSeconds = 2 });
        await new JetStreamProvisioner(client, messaging).StartAsync(timeout.Token);
        server.StreamCreated = true;
        var trigger = Options.Create(new CaptureTriggerOptions
        {
            Enabled = true,
            RigId = "rig",
            ExcavatorId = "exc",
            CoordinateSystemId = "quarry",
            ConsumerName = "capture-review"
        });
        var root = Path.Combine(Path.GetTempPath(), "capture-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            await using var app = builder.Build();
            var context = client.CreateJetStreamContext();
            var consumer = await context.CreateOrUpdateConsumerAsync(server.StreamName,
                JetStreamConsumerConfig.Durable<CaptureRequested>(trigger.Value.ConsumerName, messaging.Value), timeout.Token);
            var data = new CaptureRequested(new(Guid.NewGuid()), "Integration capture", DateTimeOffset.UtcNow,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1), RigId: "rig", ExcavatorId: "exc");
            var envelope = EventEnvelope.Create(data, "capture-integration");
            (await context.PublishAsync(EventSubjects.For<CaptureRequested>(), EventEnvelopeSerializer.Serialize(envelope), cancellationToken: timeout.Token)).EnsureSuccess();
            var unacknowledged = await consumer.NextAsync<byte[]>(cancellationToken: timeout.Token);
            Assert.NotNull(unacknowledged); // Simulate an interrupted consumer before it saves or acknowledges.

            var firstStore = new JsonMeasurementStore(app.Environment);
            using (var worker = Worker(firstStore))
            {
                await worker.StartAsync(timeout.Token);
                await DrainAsync();
                await worker.StopAsync(timeout.Token);
            }
            var first = await firstStore.GetAsync(data.MeasurementId.Value, timeout.Token);
            Assert.NotNull(first);
            Assert.Contains(envelope.EventId, first.ProcessedCommands);

            var reopenedStore = new JsonMeasurementStore(app.Environment);
            using (var worker = Worker(reopenedStore))
            {
                await worker.StartAsync(timeout.Token);
                // Republish the same event without broker MsgId dedup to exercise durable application idempotency.
                foreach (var replay in new[] { envelope,
                    EventEnvelope.Create(data with { MeasurementId = new(Guid.NewGuid()), RigId = "foreign" }, "capture-integration"),
                    EventEnvelope.Create(data with { MeasurementId = new(Guid.NewGuid()), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, "capture-integration") })
                    (await context.PublishAsync(EventSubjects.For<CaptureRequested>(), EventEnvelopeSerializer.Serialize(replay), cancellationToken: timeout.Token)).EnsureSuccess();
                await DrainAsync();
                await worker.StopAsync(timeout.Token);
            }
            var restored = Assert.Single(await reopenedStore.GetRecentAsync(20, false, timeout.Token));
            Assert.Equal(first.Id, restored.Id);
            Assert.Equal(first.Version, restored.Version);
            Assert.Single(restored.ProcessedCommands);

            CaptureTriggerConsumer Worker(IMeasurementStore store)
            {
                var workflow = new MeasurementWorkflow(store, TimeProvider.System, Options.Create(new MeasurementWorkflowOptions()));
                var handler = new CaptureTriggerHandler(workflow, trigger, Options.Create(new PipelineOptions { RigId = "rig" }), TimeProvider.System);
                return new(client, handler, trigger, messaging, NullLogger<CaptureTriggerConsumer>.Instance);
            }

            async Task DrainAsync()
            {
                while (true)
                {
                    var current = await context.GetConsumerAsync(server.StreamName, trigger.Value.ConsumerName, timeout.Token);
                    if (current.Info.NumPending == 0 && current.Info.NumAckPending == 0) return;
                    await Task.Delay(50, timeout.Token);
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
