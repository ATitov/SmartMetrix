using System.Collections.Concurrent;
using SmartMetrix.Contracts;

namespace SmartMetrix.Messaging;

public sealed record OutboxMessage(Guid EventId, string Subject, byte[] Payload, DateTimeOffset CreatedAt);

public interface IOutboxStore
{
    Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int limit, CancellationToken cancellationToken = default);
    Task MarkPublishedAsync(Guid eventId, CancellationToken cancellationToken = default);
}

public interface IInboxStore
{
    Task<bool> TryBeginAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task CompleteAsync(Guid eventId, CancellationToken cancellationToken = default);
    Task AbandonAsync(Guid eventId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryInboxStore : IInboxStore
{
    private readonly ConcurrentDictionary<Guid, byte> _processing = new();
    private readonly ConcurrentDictionary<Guid, byte> _completed = new();

    public Task<bool> TryBeginAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(!_completed.ContainsKey(eventId) && _processing.TryAdd(eventId, 0));

    public Task CompleteAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        _completed.TryAdd(eventId, 0);
        _processing.TryRemove(eventId, out _);
        return Task.CompletedTask;
    }

    public Task AbandonAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        _processing.TryRemove(eventId, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<Guid, OutboxMessage> _pending = new();

    public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        _pending.TryAdd(message.EventId, message);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>(_pending.Values.OrderBy(x => x.CreatedAt).Take(limit).ToArray());

    public Task MarkPublishedAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        _pending.TryRemove(eventId, out _);
        return Task.CompletedTask;
    }
}

public sealed class IdempotentEventProcessor<T>(IInboxStore inbox, Func<EventEnvelope<T>, CancellationToken, Task> handler)
{
    public async Task<bool> HandleAsync(EventEnvelope<T> envelope, CancellationToken cancellationToken = default)
    {
        if (!await inbox.TryBeginAsync(envelope.EventId, cancellationToken)) return false;

        try
        {
            await handler(envelope, cancellationToken);
            await inbox.CompleteAsync(envelope.EventId, cancellationToken);
            return true;
        }
        catch
        {
            await inbox.AbandonAsync(envelope.EventId, cancellationToken);
            throw;
        }
    }
}
