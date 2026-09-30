using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using SmartMetrix.CloudSyncService;
using SmartMetrix.ControlPointService;
using SmartMetrix.Domain;
using SmartMetrix.MeasurementOrchestrator;
using SmartMetrix.Messaging;
using SmartMetrix.Persistence;
using SmartMetrix.StorageService;

namespace SmartMetrix.ArchitectureTests;

public sealed partial class PostgresPersistenceTests
{
    [PostgresFact]
    public async Task OutboxFailureRollsBackMeasurementAndHistory()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("measurement");
        var store = new PostgresMeasurementStore(db);
        var original = Measurement();
        Assert.True(await store.TryCreateAsync(original));
        // Fail the LAST write, after current state and history were written.
        await Sql(db, "ALTER TABLE measurement.outbox ADD CONSTRAINT injected_failure CHECK(false) NOT VALID");
        var error = await Assert.ThrowsAsync<PostgresException>(() => store.TrySaveAsync(original with { Version = 2, Status = MeasurementStatus.Capturing }, 1));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(1, (await store.GetAsync(original.Id))!.Version);
        Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM measurement.history"));
        Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM measurement.outbox"));
        var other = Measurement();
        await Assert.ThrowsAsync<PostgresException>(() => store.TryCreateAsync(other));
        Assert.Null(await store.GetAsync(other.Id));
        await Sql(db, "ALTER TABLE measurement.outbox DROP CONSTRAINT injected_failure");
        Assert.True(await store.TrySaveAsync(original with { Version = 2 }, 1));
        Assert.Equal(2L, await Scalar(db, "SELECT count(*) FROM measurement.outbox"));
    }

    [PostgresFact]
    public async Task ConcurrentDocumentUpdatesHaveNoLostWritesAndCancelledWaitReleasesConnection()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("operator");
        using var replica = await fixture.OpenAsync("operator");
        await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            await using var session = await (i % 2 == 0 ? db : replica).OpenAsync("counter", () => 0);
            session.Value++;
            await session.CommitAsync();
        }));
        Assert.Equal(16, await replica.ReadAsync<int>("counter"));
        await using (var held = await db.OpenAsync("counter", () => 0))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replica.OpenAsync("counter", () => 0, cancellation.Token));
            held.Value = 999; // no commit
            await replica.PutAsync("independent-key", 7);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var next = await replica.OpenAsync("counter", () => 0, deadline.Token);
        Assert.Equal(16, next.Value);
        Assert.Equal(7, await db.ReadAsync<int>("independent-key"));
    }

    [PostgresFact]
    public async Task CancelledPublishIsRetryableWithSameEventIdentityAfterRestart()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("measurement");
        await new PostgresMeasurementStore(db).TryCreateAsync(Measurement());
        var outbox = new PostgresOutboxStore(db);
        var original = Assert.Single(await outbox.GetPendingAsync(10));
        var blocking = new BlockingPublisher();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatch = outbox.DispatchOneAsync(blocking, cancellation.Token);
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        using var reopened = await fixture.OpenAsync("measurement");
        var retry = new PostgresOutboxStore(reopened);
        Assert.Equal(original.EventId, Assert.Single(await retry.GetPendingAsync(10)).EventId);
        Assert.Equal(0, await Scalar(db, "SELECT attempts FROM measurement.outbox"));
        Assert.True(await retry.DispatchOneAsync(new TestPublisher(false)));
        Assert.Empty(await retry.GetPendingAsync(10));
    }

    [PostgresFact]
    public async Task LockedOutboxRowDoesNotBlockAnotherEvent()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("measurement");
        var store = new PostgresMeasurementStore(db);
        await store.TryCreateAsync(Measurement());
        await store.TryCreateAsync(Measurement());
        var outbox = new PostgresOutboxStore(db);
        var blocking = new BlockingPublisher();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = outbox.DispatchOneAsync(blocking, cancellation.Token);
        try
        {
            await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var publisher = new TestPublisher(false);
            Assert.True(await outbox.DispatchOneAsync(publisher).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, publisher.Calls);
        }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Single(await outbox.GetPendingAsync(10));
    }

    [PostgresFact]
    public async Task InboxDeduplicationIsPerConsumerAndSurvivesReopening()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("operator");
        var id = Guid.NewGuid();
        var inbox = new PostgresInboxProcessor(db);
        var calls = 0;
        Task Handler(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) { calls++; return Task.CompletedTask; }
        Assert.True(await inbox.ProcessAsync("consumer-a", id, Handler));
        using var reopened = await fixture.OpenAsync("operator");
        Assert.False(await new PostgresInboxProcessor(reopened).ProcessAsync("consumer-a", id, Handler));
        Assert.True(await inbox.ProcessAsync("consumer-b", id, Handler));
        Assert.Equal(2, calls);
        Assert.Equal(2L, await Scalar(db, "SELECT count(*) FROM operator.inbox"));
    }

    [PostgresFact]
    public async Task BrokenLegacyRegistryLeavesSourceAndDestinationUntouchedThenCanRetry()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("operator");
        await Sql(db, """
            INSERT INTO operator.documents(key,payload) VALUES('users',
              '[{"username":"alice","enabled":true,"role":"operator"},{"username":"broken","role":"operator"}]')
            """);
        await Assert.ThrowsAsync<PostgresException>(() => db.MigrateAsync());
        Assert.Equal(0L, await Scalar(db, "SELECT count(*) FROM operator.accounts"));
        Assert.Equal(0L, await Scalar(db, "SELECT count(*) FROM operator.legacy_documents"));
        Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM operator.documents WHERE key='users'"));
        await Sql(db, "UPDATE operator.documents SET payload=payload-'1'::int WHERE key='users'");
        await db.MigrateAsync();
        Assert.Single((await db.ReadAsync<JsonElement[]>("users"))!);
        Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM operator.legacy_documents"));
    }

    [PostgresFact]
    public async Task ImportConflictDoesNotOverwriteMeasurementOrMarkImportComplete()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("measurement");
        var original = Measurement();
        var store = new PostgresMeasurementStore(db);
        await store.TryCreateAsync(original);
        var directory = Path.Combine(fixture.Root, "data", "measurements");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "measurement.json");
        var json = JsonSerializer.Serialize(original with { Version = 9 }, PostgresDatabase.Json);
        await File.WriteAllTextAsync(path, json);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Persistence:ImportLegacyFiles"] = "true" }).Build();
        var importer = new LegacyDataImporter(db, config, new TestEnvironment(fixture.Root));
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.StartAsync(default));
        Assert.Equal(1, (await store.GetAsync(original.Id))!.Version);
        Assert.Equal(json, await File.ReadAllTextAsync(path));
        Assert.False(await db.ReadAsync<bool>("legacy-import-v1"));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(original, PostgresDatabase.Json));
        await importer.StartAsync(default);
        Assert.True(await db.ReadAsync<bool>("legacy-import-v1"));
        Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM measurement.history"));
    }

    [PostgresFact]
    public async Task InvalidBlockProjectionRollsBackStageAndCanBeRetried()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("measurement");
        var measurement = Measurement();
        await new PostgresMeasurementStore(db).TryCreateAsync(measurement);
        var stages = new PostgresStageStore(db);
        var run = Guid.NewGuid();
        var block = new { blockId = Guid.NewGuid(), isValid = true };
        var invalid = JsonSerializer.SerializeToElement(new { analysis = new { blocks = new[] { block, block } } });
        var error = await Assert.ThrowsAsync<PostgresException>(() => stages.SaveAsync(measurement.Id, run, "result", "s3://result", invalid, default));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Null(await stages.ReadAsync(run, "result", default));
        Assert.Equal(0L, await Scalar(db, "SELECT count(*) FROM measurement.block_records"));
        var valid = JsonSerializer.SerializeToElement(new { analysis = new { blocks = new[] { block } } });
        await stages.SaveAsync(measurement.Id, run, "result", "s3://result", valid, default);
        await stages.SaveAsync(measurement.Id, run, "result", "s3://result", valid, default);
        Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM measurement.block_records"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stages.SaveAsync(measurement.Id, run, "result", "s3://other", valid, default));
    }

    [PostgresFact]
    public async Task ArtifactCatalogRejectsMutationAndPreservesOriginalAcrossRestart()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("storage");
        var value = new ArtifactMetadata(Guid.NewGuid(), "result.json", "s3://bucket/result", new string('a', 64), 12, "application/json", "test", DateTimeOffset.UtcNow);
        var key = "artifact:measurements/" + value.MeasurementId + "/result.json";
        await db.PutAsync(key, value);
        await db.PutAsync(key, value);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.PutAsync(key, value with { Sha256 = new string('b', 64) }));
        using var reopened = await fixture.OpenAsync("storage");
        Assert.Equal(value, await reopened.ReadAsync<ArtifactMetadata>(key));
        Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM storage.artifacts"));
    }

    [PostgresFact]
    public async Task QueueSelectsDueResultsBeforeRawFramesAndSkipsTerminalItems()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("cloud_sync");
        var file = Path.Combine(fixture.Root, "artifact");
        await File.WriteAllTextAsync(file, "payload");
        using var queue = new SyncQueueStore(Options.Create(new CloudSyncOptions { QueuePath = Path.Combine(fixture.Root, "queue") }), db);
        async Task<SyncItem> Add(SyncPriority priority) => await queue.EnqueueAsync(new(Guid.NewGuid(), 1, null, [new(file, "application/octet-stream", priority)]), default);
        var raw = await Add(SyncPriority.RawFrame);
        var result = await Add(SyncPriority.Result);
        var later = await Add(SyncPriority.Result);
        var now = DateTimeOffset.UtcNow;
        await queue.SaveAsync(later with { State = SyncState.Retry, NextAttemptAt = now.AddHours(1) }, "retry", null, default);
        Assert.Equal(result.Id, (await queue.GetNextAsync(now, default))!.Id);
        await queue.SaveAsync(result with { State = SyncState.Completed, CompletedAt = now }, "done", null, default);
        Assert.Equal(raw.Id, (await queue.GetNextAsync(now, default))!.Id);
        await queue.SaveAsync(raw with { State = SyncState.Conflict }, "conflict", null, default);
        Assert.Null(await queue.GetNextAsync(now, default));
        Assert.Equal(later.Id, (await queue.GetNextAsync(now.AddHours(2), default))!.Id);
        var status = await queue.GetStatusAsync(default);
        Assert.Equal(1, status.PendingItems);
        Assert.Equal(7, status.PendingBytes);
        Assert.Equal(1, status.Conflicts);
        Assert.Collection(await queue.GetAuditAsync(2, default),
            entry => Assert.Equal("conflict", entry.Action), entry => Assert.Equal("done", entry.Action));
    }

    [PostgresFact]
    public async Task QueueAuditFailureRollsBackProgressAndAllowsRetry()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("cloud_sync");
        var file = Path.Combine(fixture.Root, "artifact");
        await File.WriteAllTextAsync(file, "payload");
        using var queue = new SyncQueueStore(Options.Create(new CloudSyncOptions { QueuePath = Path.Combine(fixture.Root, "queue") }), db);
        var item = await queue.EnqueueAsync(new(Guid.NewGuid(), 1, null, [new(file, "application/json")]), default);
        await Sql(db, "ALTER TABLE cloud_sync.audit_entries ADD CONSTRAINT injected_failure CHECK(false) NOT VALID");
        await Assert.ThrowsAsync<PostgresException>(() => queue.SaveAsync(item with { State = SyncState.Completed }, "done", null, default));
        Assert.Equal(SyncState.Pending, (await queue.GetNextAsync(DateTimeOffset.UtcNow, default))!.State);
        Assert.Single(await queue.GetAuditAsync(10, default));
        await Sql(db, "ALTER TABLE cloud_sync.audit_entries DROP CONSTRAINT injected_failure");
        await queue.SaveAsync(item with { State = SyncState.Completed }, "done", null, default);
        Assert.Null(await queue.GetNextAsync(DateTimeOffset.UtcNow, default));
        Assert.Equal(2, (await queue.GetAuditAsync(10, default)).Count);
    }

    [PostgresFact]
    public async Task UsedControlPointVersionCannotBeDeletedEvenThroughPersistenceApi()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("control_point");
        var registry = new ControlPointRegistry(TimeProvider.System, db);
        registry.Create(new("CP-1", new(1, 2, 3), "quarry", 2, DateTimeOffset.UtcNow, ControlPointStatus.Active,
            new(ControlTargetType.Prism, "P-1")), "test");
        registry.RecordUsage("cp-1", Guid.NewGuid(), "test");
        await using (var session = await db.OpenAsync<ControlPointRegistry.StoredState>("registry", () => throw new InvalidOperationException()))
        {
            session.Value.Points.Clear();
            var error = await Assert.ThrowsAsync<PostgresException>(() => session.CommitAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }
        Assert.Equal(1, new ControlPointRegistry(TimeProvider.System, db).Get("CP-1").Version);
        Assert.Equal(1L, await Scalar(db, "SELECT count(*) FROM control_point.usages"));
    }

    [PostgresFact]
    public async Task MeasurementQueriesOrderFilterAndRejectInvalidVersionAdvance()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("measurement");
        var store = new PostgresMeasurementStore(db);
        var baseline = DateTimeOffset.UtcNow.AddHours(-1);
        var active = Measurement() with { UpdatedAt = baseline };
        var completed = Measurement() with { UpdatedAt = baseline.AddMinutes(1), Status = MeasurementStatus.Completed };
        var rejected = Measurement() with { UpdatedAt = baseline.AddMinutes(2), Status = MeasurementStatus.Rejected };
        var failed = Measurement() with { UpdatedAt = baseline.AddMinutes(3), Status = MeasurementStatus.Failed };
        foreach (var value in new[] { active, completed, rejected, failed }) await store.TryCreateAsync(value);
        Assert.Equal(failed.Id, Assert.Single(await store.GetRecentAsync(1, false)).Id);
        Assert.Equal(active.Id, Assert.Single(await store.GetUnfinishedAsync()).Id);
        Assert.Equal(active.Id, Assert.Single(await store.GetRecentAsync(20, true)).Id);
        Assert.Null(await store.GetAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveAsync(active with { Version = 3 }, 1));
        Assert.Equal(4L, await Scalar(db, "SELECT count(*) FROM measurement.history"));
        Assert.Equal(4L, await Scalar(db, "SELECT count(*) FROM measurement.outbox"));
    }

    private sealed class BlockingPublisher : IEventPublisher
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private static async Task Sql(PostgresDatabase db, string sql)
    {
        await using var command = db.Source.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(PostgresDatabase db, string sql)
    {
        await using var command = db.Source.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }
}
