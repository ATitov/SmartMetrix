using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Npgsql;
using SmartMetrix.Contracts;
using SmartMetrix.Persistence;

namespace SmartMetrix.Messaging;

public sealed class PostgresOutboxStore(PostgresDatabase database) : IOutboxStore
{
    public async Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.Source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await TransactionalEvents.EnqueueAsync(database.Schema, message.EventId, message.Subject, message.Payload, message.CreatedAt,
            connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var command = database.Source.CreateCommand($"SELECT event_id,subject,payload,created_at FROM {database.Schema}.outbox WHERE published_at IS NULL AND next_attempt_at<=now() ORDER BY created_at,event_id LIMIT $1");
        command.Parameters.AddWithValue(Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<OutboxMessage>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return rows;
    }

    public async Task MarkPublishedAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var command = database.Source.CreateCommand($"UPDATE {database.Schema}.outbox SET published_at=now(),last_error=NULL WHERE event_id=$1 AND published_at IS NULL");
        command.Parameters.AddWithValue(eventId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Claims one row until publish acknowledgement. A crash after ACK can
    /// redeliver the SAME event id; consumers use a transactional inbox.</summary>
    public async Task<bool> DispatchOneAsync(IEventPublisher publisher, CancellationToken ct = default)
    {
        await using var connection = await database.Source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        OutboxMessage message;
        await using (var claim = new NpgsqlCommand($"""
            SELECT event_id,subject,payload,created_at FROM {database.Schema}.outbox
            WHERE published_at IS NULL AND next_attempt_at<=now()
            ORDER BY created_at,event_id LIMIT 1 FOR UPDATE SKIP LOCKED
            """, connection, transaction))
        await using (var reader = await claim.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) return false;
            message = Read(reader);
        }
        string? error = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await publisher.PublishAsync(message, timeout.Token);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            // Error class is useful operationally and cannot disclose endpoints, credentials or payloads.
            error = exception.GetType().Name;
        }
        var sql = error is null
            ? $"UPDATE {database.Schema}.outbox SET published_at=now(),attempts=attempts+1,last_error=NULL WHERE event_id=$1"
            : $"UPDATE {database.Schema}.outbox SET attempts=attempts+1,last_error=$2,next_attempt_at=now()+make_interval(secs=>least(300.0,power(2,least(attempts+1,8)))) WHERE event_id=$1";
        await using var update = new NpgsqlCommand(sql, connection, transaction);
        update.Parameters.AddWithValue(message.EventId);
        if (error is not null) update.Parameters.AddWithValue(error);
        await update.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static OutboxMessage Read(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1),
        reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<DateTimeOffset>(3));
}

internal sealed class PostgresOutboxDispatcher(PostgresOutboxStore outbox, IEventPublisher publisher, ILogger<PostgresOutboxDispatcher> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailure = LoggerMessage.Define(LogLevel.Warning,
        new EventId(1901, "OutboxUnavailable"), "Outbox dispatch will retry; unacknowledged events remain in PostgreSQL.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await outbox.DispatchOneAsync(publisher, stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { LogFailure(logger, null); }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}

public static class PostgresMessagingExtensions
{
    public static IServiceCollection AddSmartMetrixPostgresMessaging(this IServiceCollection services, bool dispatch = true)
    {
        services.AddOptions<NatsMessagingOptions>().BindConfiguration(NatsMessagingOptions.SectionName).ValidateOnStart();
        services.AddSingleton(provider => new NatsClient(provider.GetRequiredService<IOptions<NatsMessagingOptions>>().Value.Url));
        services.AddSingleton<IEventPublisher, LazyJetStreamPublisher>();
        services.AddSingleton<PostgresOutboxStore>();
        services.AddSingleton<IOutboxStore>(provider => provider.GetRequiredService<PostgresOutboxStore>());
        services.AddSingleton<PostgresInboxProcessor>();
        if (dispatch) services.AddHostedService<PostgresOutboxDispatcher>();
        return services;
    }
}
