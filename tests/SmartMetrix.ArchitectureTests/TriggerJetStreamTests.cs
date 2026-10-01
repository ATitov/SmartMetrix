using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using SmartMetrix.Contracts;
using SmartMetrix.Messaging;
using SmartMetrix.TriggerService;

namespace SmartMetrix.ArchitectureTests;

public sealed class NatsFactAttribute : FactAttribute
{
    public NatsFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SMARTMETRIX_TEST_NATS")) &&
            Environment.GetEnvironmentVariable("SMARTMETRIX_RUN_INTEGRATION_TESTS") != "true")
            Skip = "Requires an isolated SMARTMETRIX_TEST_NATS server or Docker integration tests.";
    }
}

[Collection("NatsInfrastructure")]
public sealed class TriggerJetStreamTests
{
    [Fact]
    public async Task UnavailableBrokerIsBoundedByRequestLifetime()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SmartMetrix:Messaging:Url"] = $"nats://127.0.0.1:{port}",
            ["Trigger:DebounceMilliseconds"] = "0",
            ["Trigger:RequestLifetimeMilliseconds"] = "100",
            ["Trigger:PublishTimeoutMilliseconds"] = "50"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddTriggerService(config);
        await using var provider = services.BuildServiceProvider();
        await Assert.ThrowsAsync<TriggerPublicationException>(() => provider.GetRequiredService<TriggerCoordinator>()
            .EvaluateAsync(new(0, 0, .05, .1, 20, true, true, false, false), default)
            .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [NatsFact]
    [Trait("Category", "Integration")]
    public async Task DirectTriggerPublicationIsStoredAndRepeatedEventIdIsDeduplicated()
    {
        IContainer? container = null;
        var url = Environment.GetEnvironmentVariable("SMARTMETRIX_TEST_NATS");
        if (string.IsNullOrWhiteSpace(url))
        {
            container = new ContainerBuilder("nats:2.11-alpine").WithCommand("--jetstream", "--store_dir=/data")
                .WithPortBinding(4222, true).WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(4222)).Build();
            await container.StartAsync();
            url = $"nats://{container.Hostname}:{container.GetMappedPublicPort(4222)}";
        }
        var streamName = "TRIGGER_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SmartMetrix:Messaging:Url"] = url,
                ["SmartMetrix:Messaging:StreamName"] = streamName,
                ["Trigger:DebounceMilliseconds"] = "0",
                ["Trigger:RequestLifetimeMilliseconds"] = "10000",
                ["Trigger:PublishTimeoutMilliseconds"] = "5000"
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(config);
            services.AddLogging();
            services.AddTriggerService(config);
            await using var provider = services.BuildServiceProvider();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var response = await provider.GetRequiredService<TriggerCoordinator>().EvaluateAsync(
                new(0, 0, .05, .1, 20, true, true, false, false), timeout.Token);
            Assert.True(response.Decision.Accepted);
            await using var client = new NatsClient(url);
            var context = client.CreateJetStreamContext();
            try
            {
                // The consumer starts only AFTER the HTTP-equivalent call received its publish ACK.
                var consumer = await context.CreateOrUpdateConsumerAsync(streamName, new ConsumerConfig("capture-test")
                {
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    FilterSubject = EventSubjects.For<CaptureRequested>()
                }, timeout.Token);
                var delivered = await consumer.NextAsync<byte[]>(cancellationToken: timeout.Token);
                Assert.NotNull(delivered);
                var envelope = EventEnvelopeSerializer.Deserialize<CaptureRequested>(delivered.Data!);
                Assert.Equal(response.EventId, envelope.EventId);
                Assert.Equal(response.MeasurementId, envelope.Data.MeasurementId.Value);
                Assert.Equal(response.ExpiresAt, envelope.Data.ExpiresAt);
                await provider.GetRequiredService<IEventPublisher>().PublishAsync(new(envelope.EventId,
                    EventSubjects.For<CaptureRequested>(), delivered.Data!, envelope.OccurredAt), timeout.Token);
                var stream = await context.GetStreamAsync(streamName, cancellationToken: timeout.Token);
                Assert.Equal(1L, stream.Info.State.Messages);
                await delivered.AckAsync(cancellationToken: timeout.Token);
            }
            finally { await context.DeleteStreamAsync(streamName, CancellationToken.None); }
        }
        finally { if (container is not null) await container.DisposeAsync(); }
    }
}

