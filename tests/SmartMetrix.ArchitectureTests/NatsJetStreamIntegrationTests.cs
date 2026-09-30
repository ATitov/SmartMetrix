using DotNet.Testcontainers.Builders;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;

namespace SmartMetrix.ArchitectureTests;

[Collection("NatsInfrastructure")]
public sealed class NatsJetStreamIntegrationTests
{
    [NatsFact]
    [Trait("Category", "Integration")]
    public async Task DurableConsumerReceivesEventPublishedBeforeConsumerStarts()
    {
        await using var fixture = await NatsTestServer.CreateAsync();
        var url = fixture.Url;
        await using var publisher = new NatsClient(url);
        var publisherContext = publisher.CreateJetStreamContext();
        await publisherContext.CreateStreamAsync(
            new StreamConfig(fixture.StreamName, ["integration.>"]),
            CancellationToken.None);
        fixture.StreamCreated = true;
        (await publisherContext.PublishAsync(
            "integration.event",
            "persisted",
            cancellationToken: CancellationToken.None)).EnsureSuccess();

        await using var consumerClient = new NatsClient(url);
        var consumerContext = consumerClient.CreateJetStreamContext();
        var consumer = await consumerContext.CreateOrUpdateConsumerAsync(
            fixture.StreamName,
            new ConsumerConfig("integration-consumer"),
            CancellationToken.None);
        var message = await consumer.NextAsync<string>(cancellationToken: CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal("persisted", message.Data);
        await message.AckAsync(cancellationToken: CancellationToken.None);
    }
}
