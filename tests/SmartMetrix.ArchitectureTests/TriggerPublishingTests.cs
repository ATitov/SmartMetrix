using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartMetrix.Contracts;
using SmartMetrix.Domain;
using SmartMetrix.Messaging;
using SmartMetrix.Persistence;
using SmartMetrix.TriggerService;

namespace SmartMetrix.ArchitectureTests;

public sealed partial class TriggerDecisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpEndpointReportsAcknowledgementOrUncertainDelivery(bool fail)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Trigger:DebounceMilliseconds"] = "0",
            ["Persistence:Provider"] = "Postgres",
            ["ConnectionStrings:SmartMetrix"] = "invalid-but-unused"
        });
        builder.Services.AddTriggerService(builder.Configuration);
        builder.Services.RemoveAll<IEventPublisher>();
        builder.Services.AddSingleton<IEventPublisher>(new RecordingPublisher((_, _) =>
            fail ? Task.FromException(new IOException("offline")) : Task.CompletedTask));
        await using var app = builder.Build();
        app.MapTriggerEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.PostAsJsonAsync("/v1/trigger/evaluate", Safe);
        Assert.Equal(fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, body.GetProperty("eventId").GetGuid());
        Assert.NotEqual(Guid.Empty, body.GetProperty("measurementId").GetGuid());
        if (fail)
        {
            Assert.True(body.GetProperty("deliveryUncertain").GetBoolean());
            Assert.Equal("CapturePublicationUnconfirmed", body.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task TriggerRegistersNoDatabaseOutboxOrDispatcherEvenWithPostgresConfiguration()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Postgres",
            ["ConnectionStrings:SmartMetrix"] = "invalid-but-unused"
        }).Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddTriggerService(config);
        await using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<TriggerCoordinator>());
        Assert.Null(provider.GetService<PostgresDatabase>());
        Assert.Null(provider.GetService<IOutboxStore>());
        Assert.Empty(provider.GetServices<IHostedService>());
    }

    [Fact]
    public async Task AcceptedResponseWaitsForBrokerAcknowledgement()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new RecordingPublisher(async (_, ct) =>
        {
            entered.TrySetResult();
            await ack.Task.WaitAsync(ct);
        });
        var coordinator = Coordinator(clock, publisher, new TriggerOptions { DebounceMilliseconds = 0, PublishTimeoutMilliseconds = 5000, RequestLifetimeMilliseconds = 10000 });
        var pending = coordinator.EvaluateAsync(Safe, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        ack.SetResult();
        var result = await pending;
        Assert.True(result.Decision.Accepted);
        Assert.Equal(Assert.Single(publisher.Messages).EventId, result.EventId);
        Assert.Equal(clock.GetUtcNow().AddSeconds(10), result.ExpiresAt);
    }

    [Fact]
    public async Task LostAcknowledgementRetriesIdenticalMessageAndDeadline()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var calls = 0;
        var publisher = new RecordingPublisher((_, _) => ++calls == 1 ? Task.FromException(new TimeoutException()) : Task.CompletedTask);
        var result = await Coordinator(clock, publisher).EvaluateAsync(Safe, default);
        Assert.Equal(2, publisher.Messages.Count);
        Assert.Same(publisher.Messages[0], publisher.Messages[1]);
        var envelope = EventEnvelopeSerializer.Deserialize<CaptureRequested>(publisher.Messages[0].Payload);
        Assert.Equal(result.EventId, envelope.EventId);
        Assert.Equal(result.MeasurementId, envelope.Data.MeasurementId.Value);
        Assert.Equal(clock.GetUtcNow().AddSeconds(2), envelope.Data.ExpiresAt);
    }

    [Fact]
    public async Task BrokerFailureReturnsUnconfirmedAndDoesNotQueueBackgroundReplay()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var publisher = new RecordingPublisher((_, _) => Task.FromException(new IOException("offline")));
        var coordinator = Coordinator(clock, publisher);
        var error = await Assert.ThrowsAsync<TriggerPublicationException>(() => coordinator.EvaluateAsync(Safe, default));
        Assert.Equal(3, publisher.Messages.Count);
        Assert.All(publisher.Messages, message => Assert.Equal(error.EventId, message.EventId));
        Assert.Equal("CooldownActive", (await coordinator.EvaluateAsync(Safe, default)).Decision.Reason);
        Assert.Equal(3, publisher.Messages.Count); // uncertain delivery does not release safety cooldown
    }

    [Fact]
    public async Task ExpiredRequestIsNotRetriedAndLateAcknowledgementDoesNotReturnAccepted()
    {
        foreach (var fail in new[] { false, true })
        {
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var publisher = new RecordingPublisher((_, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(2));
                return fail ? Task.FromException(new TimeoutException()) : Task.CompletedTask;
            });
            await Assert.ThrowsAsync<TriggerPublicationException>(() => Coordinator(clock, publisher).EvaluateAsync(Safe, default));
            Assert.Single(publisher.Messages);
        }
    }

    [Fact]
    public async Task CallerCancellationStopsPublicationWithoutAnotherAttempt()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var cancellation = new CancellationTokenSource();
        var publisher = new RecordingPublisher((_, ct) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(ct);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Coordinator(clock, publisher).EvaluateAsync(Safe, cancellation.Token));
        Assert.Single(publisher.Messages);
    }

    [Fact]
    public async Task UnsafeSnapshotNeverPublishes()
    {
        var publisher = new RecordingPublisher();
        var result = await Coordinator(new ManualTimeProvider(DateTimeOffset.UtcNow), publisher).EvaluateAsync(Safe with { ManualInhibit = true }, default);
        Assert.False(result.Decision.Accepted);
        Assert.Empty(publisher.Messages);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(null)]
    public async Task ConsumerDiscardsExpiredAndLegacyRequests(int? seconds)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var calls = 0;
        var processor = new IdempotentEventProcessor<CaptureRequested>(new InMemoryInboxStore(),
            (_, _) => { calls++; return Task.CompletedTask; }, clock);
        var request = new CaptureRequested(MeasurementId.New(), "test", clock.GetUtcNow(),
            ExpiresAt: seconds.HasValue ? clock.GetUtcNow().AddSeconds(seconds.Value) : null);
        Assert.False(await processor.HandleAsync(EventEnvelope.Create(request, "test")));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ConsumerRechecksDeadlineAfterWaitingForInboxAndDeduplicatesLiveRequest()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var inbox = new AdvancingInbox(clock);
        var calls = 0;
        var processor = new IdempotentEventProcessor<CaptureRequested>(inbox,
            (_, _) => { calls++; return Task.CompletedTask; }, clock);
        var request = new CaptureRequested(MeasurementId.New(), "test", clock.GetUtcNow(), ExpiresAt: clock.GetUtcNow().AddSeconds(1));
        Assert.False(await processor.HandleAsync(EventEnvelope.Create(request, "test")));
        Assert.Equal(0, calls);
        Assert.Equal(1, inbox.Completed);
        var live = EventEnvelope.Create(request with { ExpiresAt = clock.GetUtcNow().AddSeconds(10) }, "live");
        Assert.True(await processor.HandleAsync(live));
        Assert.False(await processor.HandleAsync(live));
        Assert.Equal(1, calls);
    }

    private static TriggerCoordinator Coordinator(ManualTimeProvider clock, RecordingPublisher publisher, TriggerOptions? options = null)
    {
        var configured = Options.Create(options ?? new TriggerOptions { DebounceMilliseconds = 0 });
        return new(new TriggerDecisionEngine(clock, configured), clock, publisher, configured);
    }

    private sealed class RecordingPublisher(Func<OutboxMessage, CancellationToken, Task>? publish = null) : IEventPublisher
    {
        public List<OutboxMessage> Messages { get; } = [];
        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return publish?.Invoke(message, cancellationToken) ?? Task.CompletedTask;
        }
    }

    private sealed class AdvancingInbox(ManualTimeProvider clock) : IInboxStore
    {
        private readonly InMemoryInboxStore inner = new();
        public int Completed;
        public Task<bool> TryBeginAsync(Guid eventId, CancellationToken cancellationToken = default)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return inner.TryBeginAsync(eventId, cancellationToken);
        }
        public Task CompleteAsync(Guid eventId, CancellationToken cancellationToken = default)
        {
            Completed++;
            return inner.CompleteAsync(eventId, cancellationToken);
        }
        public Task AbandonAsync(Guid eventId, CancellationToken cancellationToken = default) => inner.AbandonAsync(eventId, cancellationToken);
    }
}
