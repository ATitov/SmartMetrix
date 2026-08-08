using DotNet.Testcontainers.Builders;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;

namespace SmartMetrix.ArchitectureTests;

public sealed class NatsJetStreamIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DurableConsumerReceivesEventPublishedBeforeConsumerStarts()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SMARTMETRIX_RUN_INTEGRATION_TESTS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using var container = new ContainerBuilder("nats:2.11-alpine")
            .WithCommand("--jetstream", "--store_dir=/data")
            .WithPortBinding(4222, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(4222))
            .Build();
        await container.StartAsync(CancellationToken.None);

        var url = $"nats://{container.Hostname}:{container.GetMappedPublicPort(4222)}";
        await using var publisher = new NatsClient(url);
        var publisherContext = publisher.CreateJetStreamContext();
        await publisherContext.CreateStreamAsync(
            new StreamConfig("INTEGRATION_EVENTS", ["integration.>"]),
            CancellationToken.None);
        (await publisherContext.PublishAsync(
            "integration.event",
            "persisted",
            cancellationToken: CancellationToken.None)).EnsureSuccess();

        await using var consumerClient = new NatsClient(url);
        var consumerContext = consumerClient.CreateJetStreamContext();
        var consumer = await consumerContext.CreateOrUpdateConsumerAsync(
            "INTEGRATION_EVENTS",
            new ConsumerConfig("integration-consumer"),
            CancellationToken.None);
        var message = await consumer.NextAsync<string>(cancellationToken: CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal("persisted", message.Data);
        await message.AckAsync(cancellationToken: CancellationToken.None);
    }
}
