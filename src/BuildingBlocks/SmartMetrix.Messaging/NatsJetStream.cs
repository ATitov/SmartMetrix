using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using SmartMetrix.Contracts;

namespace SmartMetrix.Messaging;

public sealed class NatsMessagingOptions
{
    public const string SectionName = "SmartMetrix:Messaging";
    public string Url { get; set; } = "nats://localhost:4222";
    public string StreamName { get; set; } = "SMARTMETRIX_EVENTS";
    public int MaximumDeliveries { get; set; } = 5;
    public int AckWaitSeconds { get; set; } = 30;
}

public static class JetStreamConsumerConfig
{
    public static ConsumerConfig Durable<T>(string consumerName, NatsMessagingOptions options) => new(consumerName)
    {
        DurableName = consumerName,
        FilterSubject = EventSubjects.For<T>(),
        AckPolicy = ConsumerConfigAckPolicy.Explicit,
        AckWait = TimeSpan.FromSeconds(options.AckWaitSeconds),
        MaxDeliver = options.MaximumDeliveries,
        Backoff = new TimeSpan[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(10),
        }[..options.MaximumDeliveries],
    };
}

public interface IEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}

public sealed class NatsJetStreamPublisher(NatsClient client) : IEventPublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var ack = await client.CreateJetStreamContext().PublishAsync(
            message.Subject,
            message.Payload,
            opts: new NatsJSPubOpts { MsgId = message.EventId.ToString("N") },
            cancellationToken: cancellationToken);
        ack.EnsureSuccess();
    }
}

public sealed class JetStreamProvisioner(NatsClient client, IOptions<NatsMessagingOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var value = options.Value;
        var context = client.CreateJetStreamContext();
        await context.CreateOrUpdateStreamAsync(new StreamConfig(value.StreamName, [EventSubjects.All])
        {
            Storage = StreamConfigStorage.File,
            Retention = StreamConfigRetention.Limits,
            MaxMsgSize = EventEnvelopeSerializer.MaximumPayloadBytes,
            DuplicateWindow = TimeSpan.FromHours(2),
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class OutboxDispatcher(IOutboxStore outbox, IEventPublisher publisher) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var messages = await outbox.GetPendingAsync(100, stoppingToken);
            foreach (var message in messages)
            {
                await publisher.PublishAsync(message, stoppingToken);
                await outbox.MarkPublishedAsync(message.EventId, stoppingToken);
            }

            await Task.Delay(messages.Count == 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(50), stoppingToken);
        }
    }
}

public static class MessagingExtensions
{
    public static IServiceCollection AddSmartMetrixMessaging(this IServiceCollection services, Action<NatsMessagingOptions>? configure = null)
    {
        services.AddOptions<NatsMessagingOptions>().BindConfiguration(NatsMessagingOptions.SectionName).ValidateOnStart();
        if (configure is not null) services.Configure(configure);
        services.AddSingleton(provider => new NatsClient(provider.GetRequiredService<IOptions<NatsMessagingOptions>>().Value.Url));
        services.AddSingleton<IEventPublisher, NatsJetStreamPublisher>();
        services.AddSingleton<IInboxStore, InMemoryInboxStore>();
        services.AddSingleton<IOutboxStore, InMemoryOutboxStore>();
        services.AddHostedService<JetStreamProvisioner>();
        services.AddHostedService<OutboxDispatcher>();
        return services;
    }

    public static Task EnqueueAsync<T>(this IOutboxStore outbox, EventEnvelope<T> envelope, CancellationToken cancellationToken = default) =>
        outbox.EnqueueAsync(new OutboxMessage(envelope.EventId, EventSubjects.For<T>(), EventEnvelopeSerializer.Serialize(envelope), envelope.OccurredAt), cancellationToken);
}
