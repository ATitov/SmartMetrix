using System.Numerics;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Npgsql;
using SmartMetrix.ApiGateway;
using SmartMetrix.CalibrationService;
using SmartMetrix.CameraService;
using SmartMetrix.CloudSyncService;
using SmartMetrix.ControlPointService;
using SmartMetrix.Domain;
using SmartMetrix.LocalPositioningService;
using SmartMetrix.MeasurementOrchestrator;
using SmartMetrix.Persistence;
using SmartMetrix.Messaging;

namespace SmartMetrix.ArchitectureTests;

// Each HTTP rig launches a full set of services. Parallel rigs exhaust CI/desktop
// resources and turn the deliberately short pipeline timeouts into false failures.
[CollectionDefinition("ServiceProcesses", DisableParallelization = true)]
public sealed class ServiceProcessTestGroup;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SMARTMETRIX_TEST_POSTGRES")) &&
            Environment.GetEnvironmentVariable("SMARTMETRIX_RUN_INTEGRATION_TESTS") != "true")
            Skip = "Requires SMARTMETRIX_TEST_POSTGRES or Docker with SMARTMETRIX_RUN_INTEGRATION_TESTS=true.";
    }
}

[Trait("Category", "Postgres")]
[Collection("ServiceProcesses")]
public sealed partial class PostgresPersistenceTests
{
    [PostgresFact]
    public async Task CompletedCloudIntentSurvivesReopenAndAcknowledgementRemovesIt()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var database = await fixture.OpenAsync("measurement");
        var store = new PostgresMeasurementStore(database);
        var run = Guid.NewGuid();
        var item = Measurement() with
        {
            Status = MeasurementStatus.Completed,
            Pipeline = new(run, "Services", [], ResultUri: $"s3://test/measurements/{run:D}/pipeline/result.json")
        };
        Assert.True(await store.TryCreateAsync(item));
        using var reopened = await fixture.OpenAsync("measurement");
        var second = new PostgresMeasurementStore(reopened);
        Assert.Equal(item.Id, Assert.Single(await second.GetPendingCloudSyncAsync()).Id);
        Assert.True(await second.TrySaveAsync(item with { Version = item.Version + 1, CloudQueuedAt = DateTimeOffset.UtcNow }, item.Version));
        Assert.Empty(await store.GetPendingCloudSyncAsync());
    }

    [PostgresFact]
    public async Task HttpPipelineUsesPostgresAndRecoversCompletedResultAfterRestart()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await using var rig = new PipelineTestRig { PostgresConnection = fixture.Connection };
        await rig.StartAsync();
        await rig.ConfigureRigAsync();
        var request = new StartMeasurementRequest(Guid.NewGuid(), null, "EX-TEST", "quarry:test", "PostgreSQL integration");
        var started = await rig.PostAsync<MeasurementProcess>("orchestrator", "measurements", request);
        var completed = await rig.WaitForTerminalAsync(started.Id);
        Assert.True(completed.Status == MeasurementStatus.Completed, completed.FailureReason + rig.Logs);
        Assert.Null(completed.D50); // The synthetic scene is planar; no volume distribution is available.
        using var db = await fixture.OpenAsync("measurement");
        await using var count = db.Source.CreateCommand("SELECT count(*) FROM measurement.stages");
        Assert.Equal(9L, await count.ExecuteScalarAsync());
        await rig.RestartOrchestratorAsync();
        var restored = await rig.GetAsync<MeasurementProcess>("orchestrator", $"measurements/{started.Id}");
        Assert.Equal(completed.Version, restored.Version);
        Assert.Equal(completed.D50, restored.D50);
        var result = await rig.GetAsync<JsonElement>("orchestrator", $"measurements/{started.Id}/stages/result");
        Assert.Equal(JsonValueKind.Null, result.GetProperty("analysis").GetProperty("d50Millimetres").ValueKind);
        Assert.False(result.GetProperty("analysis").GetProperty("volumeDistributionAvailable").GetBoolean());
    }

    [PostgresFact]
    public async Task ConcurrentWritersHaveOneWinnerAndHistoryIsAtomic()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var databases = await Task.WhenAll(fixture.OpenAsync("measurement"), fixture.OpenAsync("measurement"));
        using var db = databases[0];
        await db.MigrateAsync(); // Repeated startup is safe.
        using var replica = databases[1];
        var first = new PostgresMeasurementStore(db);
        var second = new PostgresMeasurementStore(replica);
        var value = Measurement();
        Assert.True(await first.TryCreateAsync(value));
        Assert.False(await second.TryCreateAsync(value));
        var updates = await Task.WhenAll(first.TrySaveAsync(value with { Version = 2, Status = MeasurementStatus.Capturing }, 1),
            second.TrySaveAsync(value with { Version = 2, Status = MeasurementStatus.Rejected }, 1));
        Assert.Single(updates, x => x);
        Assert.Equal(2, (await second.GetAsync(value.Id))!.Version);
        await using var count = db.Source.CreateCommand("SELECT count(*) FROM measurement.history");
        Assert.Equal(2L, await count.ExecuteScalarAsync());
        Assert.False(await first.TrySaveAsync(value with { Version = 2 }, 1));
        Assert.Single(await first.GetRecentAsync(1, false));
        var terminal = (await first.GetAsync(value.Id))! with { Version = 3, Status = MeasurementStatus.Completed };
        Assert.True(await first.TrySaveAsync(terminal, 2));
        Assert.Empty(await second.GetUnfinishedAsync());
    }

    [PostgresFact]
    public async Task StageResultsAreImmutableAndQueryableWithoutMinio()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("measurement");
        var measurement = Measurement();
        Assert.True(await new PostgresMeasurementStore(db).TryCreateAsync(measurement));
        var stage = new PostgresStageStore(db);
        var run = Guid.NewGuid();
        var block = Guid.NewGuid();
        var result = JsonSerializer.SerializeToElement(new
        {
            isTestData = false,
            coordinateSystemId = "quarry",
            calibrationId = "cal-v1",
            modelVersion = "model-v1",
            analysis = new
            {
                d10Millimetres = 10,
                d50Millimetres = 50,
                d80Millimetres = 80,
                d95Millimetres = 95,
                confidence = .9,
                oversizeCount = 0,
                sizeClasses = new { small = 1 },
                provenance = new { version = "v1" },
                blocks = new[] { new { blockId = block, isValid = true, isPartiallyVisible = false, confidence = .9,
                    geometry = new { equivalentDiameterMillimetres = 50 }, qualityReasons = Array.Empty<string>() } }
            },
            georeference = new { blocks = new[] { new { blockId = block, centre = new { xMetres = 1, yMetres = 2, zMetres = 3 }, positionUncertaintyMetres = .01 } } }
        });
        await stage.SaveAsync(measurement.Id, run, "result", "s3://test/result", result, default);
        await stage.SaveAsync(measurement.Id, run, "result", "s3://test/result", result, default);
        Assert.NotNull(await stage.ReadAsync(run, "result", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stage.SaveAsync(measurement.Id, run, "result", "s3://test/result", JsonSerializer.SerializeToElement(new { changed = true }), default));
        await using var query = db.Source.CreateCommand("SELECT diameter_mm, x_metres, coordinate_system_id FROM measurement.blocks");
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(50, reader.GetDouble(0)); Assert.Equal(1, reader.GetDouble(1)); Assert.Equal("quarry", reader.GetString(2));
    }

    [PostgresFact]
    public async Task RegistryStateSurvivesRecreationAndFailedMutationsRollback()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("calibration");
        var options = Options.Create(new CalibrationOptions());
        var registry = new CalibrationRegistry(options, TimeProvider.System, db);
        var payload = new CalibrationPayload("rig", [Camera("A", 0), Camera("B", .7), Camera("C", 1.5)],
            new(.7, .8, 1.5), new([1, 0, 0, 0, 1, 0, 0, 0, 1], [0, 0, 0]), .3);
        var record = registry.Create(payload, "alice");
        registry.Activate(record.Id, DateTimeOffset.UtcNow.AddMinutes(-1), null, "alice");
        using var replica = await fixture.OpenAsync("calibration");
        var restarted = new CalibrationRegistry(options, TimeProvider.System, replica);
        Assert.Equal(record.Id, restarted.GetActive("rig", DateTimeOffset.UtcNow)!.Id);
        Assert.True(CalibrationRegistry.Verify(restarted.Export(record.Id)));
        var another = restarted.Create(payload, "bob");
        Assert.Throws<CalibrationConflictException>(() => restarted.Activate(another.Id, DateTimeOffset.UtcNow, null, "bob"));
        Assert.Equal(CalibrationStatus.Draft, registry.Get(another.Id).Status);
        Assert.Single(registry.AuditLog(another.Id));
        using var points = await fixture.OpenAsync("control_point");
        var controls = new ControlPointRegistry(TimeProvider.System, points);
        controls.Create(new("CP-1", new(1, 2, 3), "quarry", 2, DateTimeOffset.UtcNow, ControlPointStatus.Active, new(ControlTargetType.Prism, "P-1")), "alice");
        Assert.Equal(1, controls.RecordUsage("cp-1", Guid.NewGuid(), "alice").PointVersion);
        Assert.Throws<ControlPointConflictException>(() => new ControlPointRegistry(TimeProvider.System, points).Delete("CP-1", "bob"));
        Assert.Equal(1, controls.Get("cp-1").Version);
        using var position = await fixture.OpenAsync("positioning");
        var transforms = new TransformRegistry(position);
        transforms.Add(new("exc", "quarry", 1, new(new Vector3(1, 2, 3), Quaternion.Identity), DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(new Vector3(1, 2, 3), new TransformRegistry(position).Get("exc", DateTimeOffset.UtcNow).ExcavatorToQuarry.TranslationMetres);
    }

    [PostgresFact]
    public async Task UserBootstrapAndUpdatesAreSharedAcrossInstances()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("operator");
        using var replica = await fixture.OpenAsync("operator");
        var options = Options.Create(new OperatorApiOptions { BootstrapAdminPassword = "test-admin-password" });
        var environment = new TestEnvironment(fixture.Root);
        using var first = new UserAccountStore(options, environment, db);
        using var second = new UserAccountStore(options, environment, replica);
        await Task.WhenAll(first.InitializeAsync(default), second.InitializeAsync(default));
        await Task.WhenAll(first.AddAsync(new("alice", "Alice", "operator", "test-alice-password"), default),
            second.AddAsync(new("boris", "Boris", "engineer", "test-boris-password"), default));
        Assert.Equal(3, (await first.ListAsync(default)).Count);
        Assert.NotNull(await second.AuthenticateAsync("alice", "test-alice-password", default));
        await first.SetEnabledAsync("alice", false, default);
        Assert.Null(await second.AuthenticateAsync("alice", "test-alice-password", default));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "data", "users.json")));
        await using (var transaction = await db.OpenAsync("rollback-test", () => new List<int>())) transaction.Value.Add(1);
        Assert.Null(await replica.ReadAsync<List<int>>("rollback-test"));
        var tools = new EngineeringTools(options, environment, db);
        await tools.SaveConfigurationAsync(new() { ["camera.adapter"] = "Simulator" }, default);
        Assert.Equal("Simulator", (await new EngineeringTools(options, environment, replica).GetConfigurationAsync(default))["camera.adapter"]);
    }

    [PostgresFact]
    public async Task LegacyImportIsRepeatableAndLeavesSourceIntact()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("measurement");
        var value = Measurement();
        var directory = Path.Combine(fixture.Root, "data", "measurements");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{value.Id:N}.json");
        var json = JsonSerializer.Serialize(value, PostgresDatabase.Json);
        await File.WriteAllTextAsync(path, json);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Persistence:ImportLegacyFiles"] = "true" }).Build();
        var importer = new LegacyDataImporter(db, config, new TestEnvironment(fixture.Root));
        await importer.StartAsync(default);
        await importer.StartAsync(default);
        Assert.Equal(json, await File.ReadAllTextAsync(path));
        Assert.Single(await new PostgresMeasurementStore(db).GetRecentAsync(20, false));
        Assert.Equal(value.Id, (await new PostgresMeasurementStore(db).GetAsync(value.Id))!.Id);
    }

    [PostgresFact]
    public async Task QueueAndInterruptedCaptureSurviveRestart()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("cloud_sync");
        var file = Path.Combine(fixture.Root, "result.json");
        await File.WriteAllTextAsync(file, "{\"result\":1}");
        var options = Options.Create(new CloudSyncOptions { QueuePath = Path.Combine(fixture.Root, "queue") });
        using var first = new SyncQueueStore(options, db);
        var request = new EnqueueSyncRequest(Guid.NewGuid(), 1, null, [new(file, "application/json")]);
        var item = await first.EnqueueAsync(request, default);
        using var restarted = new SyncQueueStore(options, db);
        Assert.Equal(item.Id, (await restarted.EnqueueAsync(request, default)).Id);
        Assert.Equal(1, (await restarted.GetStatusAsync(default)).PendingItems);
        await restarted.SaveAsync(item with { State = SyncState.Completed, CompletedAt = DateTimeOffset.UtcNow }, "done", null, default);
        Assert.Equal(0, (await first.GetStatusAsync(default)).PendingItems);
        Assert.Equal(2, (await first.GetAuditAsync(20, default)).Count);
        using var camera = await fixture.OpenAsync("camera");
        var id = Guid.NewGuid();
        await camera.PutAsync($"capture:{id:N}", new CaptureReceiptStore.StoredCapture("cal", null));
        using var receipts = new CaptureReceiptStore(new TestEnvironment(fixture.Root), null!, camera);
        var capture = new CaptureRequest { CalibrationId = "cal" };
        var error = await Assert.ThrowsAsync<CameraCaptureException>(() => receipts.CaptureAsync(id, capture, default));
        Assert.Equal("CaptureOutcomeUnknown", error.Code);
    }

    [PostgresFact]
    public async Task OperatorImportReviewsAndAuditPreserveIdentity()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("operator");
        var environment = new TestEnvironment(fixture.Root);
        var data = Path.Combine(fixture.Root, "data");
        Directory.CreateDirectory(data);
        var options = Options.Create(new OperatorApiOptions { BootstrapAdminPassword = "legacy-admin-password" });
        using (var files = new UserAccountStore(options, environment)) await files.InitializeAsync(default);
        var entry = new AuditEntry(DateTimeOffset.UtcNow, "admin", "administrator", "legacy-action", null, null, true);
        await File.WriteAllTextAsync(Path.Combine(data, "operator-audit.jsonl"), JsonSerializer.Serialize(entry, PostgresDatabase.Json) + Environment.NewLine);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Persistence:ImportLegacyFiles"] = "true" }).Build();
        var importer = new LegacyDataImporter(db, configuration, environment);
        await importer.StartAsync(default);
        await importer.StartAsync(default);
        using var users = new UserAccountStore(Options.Create(new OperatorApiOptions()), environment, db);
        await users.InitializeAsync(default); // Imported admin does not require a new bootstrap password.
        Assert.NotNull(await users.AuthenticateAsync("admin", "legacy-admin-password", default));
        using var audit = new JsonAuditStore(environment, options, db);
        Assert.Single(await audit.ReadAsync(default));
        await audit.AppendAsync(entry with { Action = "new-action" }, default);
        Assert.Equal(2, (await audit.ReadAsync(default)).Count);
        using var first = new WorkstationReviewStore(Options.Create(new WorkstationOptions()), environment, db);
        using var second = new WorkstationReviewStore(Options.Create(new WorkstationOptions()), environment, db);
        var review = new ReviewRecord("scope", Guid.NewGuid(), Guid.NewGuid(), 3, "Approved", "checked", "admin", DateTimeOffset.UtcNow);
        static Task<(long, string)> Current() => Task.FromResult((3L, "Completed"));
        var replies = await Task.WhenAll(first.SaveAsync(review, Current, default), second.SaveAsync(review, Current, default));
        Assert.All(replies, value => Assert.Equal(review, value));
        Assert.Single(await second.ListAsync("scope", review.MeasurementId, default));
        var conflict = await Assert.ThrowsAsync<WorkstationApiException>(() => first.SaveAsync(review with { Decision = "NeedsRevision" }, Current, default));
        Assert.Equal("CommandConflict", conflict.Code);
    }


    [PostgresFact]
    public async Task OutboxRetriesAndInboxRollsBackThenDeduplicatesConcurrentDeliveries()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("measurement");
        var store = new PostgresMeasurementStore(db);
        var value = Measurement();
        Assert.True(await store.TryCreateAsync(value));
        Assert.False(await store.TryCreateAsync(value));
        var outbox = new PostgresOutboxStore(db);
        var message = Assert.Single(await outbox.GetPendingAsync(10));
        await outbox.EnqueueAsync(message); // identical retry is safe
        await Assert.ThrowsAsync<InvalidOperationException>(() => outbox.EnqueueAsync(message with { Payload = [1] }));
        Assert.True(await outbox.DispatchOneAsync(new TestPublisher(true)));
        await using (var failure = db.Source.CreateCommand("SELECT attempts FROM measurement.outbox WHERE published_at IS NULL"))
            Assert.Equal(1, await failure.ExecuteScalarAsync());
        Assert.Empty(await outbox.GetPendingAsync(10)); // persisted backoff
        await using (var due = db.Source.CreateCommand("UPDATE measurement.outbox SET next_attempt_at=now()")) await due.ExecuteNonQueryAsync();
        var publisher = new TestPublisher(false);
        var dispatch = await Task.WhenAll(outbox.DispatchOneAsync(publisher), new PostgresOutboxStore(db).DispatchOneAsync(publisher));
        Assert.Single(dispatch, x => x);
        Assert.Equal(1, publisher.Calls);
        Assert.Empty(await outbox.GetPendingAsync(10));

        var inbox = new PostgresInboxProcessor(db);
        async Task Effect(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand("INSERT INTO measurement.events(payload) VALUES('{}')", connection, transaction);
            await command.ExecuteNonQueryAsync(ct);
        }
        await Assert.ThrowsAsync<IOException>(() => inbox.ProcessAsync("test", message.EventId, async (c, t, ct) =>
        {
            await Effect(c, t, ct);
            throw new IOException("simulated crash");
        }));
        var deliveries = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => inbox.ProcessAsync("test", message.EventId, Effect)));
        Assert.Single(deliveries, x => x);
        await using var effects = db.Source.CreateCommand("SELECT count(*) FROM measurement.events");
        Assert.Equal(1L, await effects.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task LegacyRegistryUpgradeIsAtomicAndRejectsOldWriters()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var db = await fixture.OpenAsync("operator");
        const string json = "[{\"username\":\"ADMIN\",\"enabled\":true,\"role\":\"administrator\"}]";
        async Task OldWrite()
        {
            await using var old = db.Source.CreateCommand("INSERT INTO operator.documents(key,payload) VALUES('users',$1::jsonb)");
            old.Parameters.AddWithValue(json);
            await old.ExecuteNonQueryAsync();
        }
        await OldWrite();
        await db.MigrateAsync();
        var users = await db.ReadAsync<JsonElement[]>("users");
        Assert.Equal("ADMIN", Assert.Single(users!).GetProperty("username").GetString());
        await using (var count = db.Source.CreateCommand("SELECT count(*) FROM operator.accounts")) Assert.Equal(1L, await count.ExecuteScalarAsync());
        await using (var count = db.Source.CreateCommand("SELECT count(*) FROM operator.documents WHERE key='users'")) Assert.Equal(0L, await count.ExecuteScalarAsync());
        await db.MigrateAsync();
        await OldWrite();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.MigrateAsync());
        Assert.Single((await db.ReadAsync<JsonElement[]>("users"))!);
    }

    private sealed class TestPublisher(bool fail) : IEventPublisher
    {
        public int Calls;
        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return fail ? Task.FromException(new IOException("offline")) : Task.CompletedTask;
        }
    }

    private static MeasurementProcess Measurement() => new(Guid.NewGuid(), "exc", "quarry", MeasurementStatus.Requested,
        1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, [], []);
    private static CameraCalibration Camera(string id, double x) => new(id, new(1000, 1000, 640, 360, 1280, 720),
        [0, 0, 0, 0, 0], [1, 0, 0, 0, 1, 0, 0, 0, 1], [x, 0, 0], $"s3://calibration/{id}.map");

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestDatabase(string adminConnection, string name, string connection, IContainer? container) : IAsyncDisposable
    {
        public string Connection => connection;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "smartmetrix-pg-" + Guid.NewGuid().ToString("N"));

        public static async Task<TestDatabase> CreateAsync()
        {
            var admin = Environment.GetEnvironmentVariable("SMARTMETRIX_TEST_POSTGRES");
            IContainer? container = null;
            if (string.IsNullOrWhiteSpace(admin))
            {
                container = new ContainerBuilder("postgis/postgis:17-3.5-alpine")
                    .WithEnvironment("POSTGRES_PASSWORD", "integration-only-password")
                    .WithPortBinding(5432, true)
                    // The initdb server accepts Unix-socket connections before the final TCP server starts.
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5432))
                    .Build();
                await container.StartAsync();
                admin = $"Host={container.Hostname};Port={container.GetMappedPublicPort(5432)};Username=postgres;Password=integration-only-password;Database=postgres";
            }
            var name = "smartmetrix_test_" + Guid.NewGuid().ToString("N");
            await using var source = NpgsqlDataSource.Create(admin);
            await using var create = source.CreateCommand($"CREATE DATABASE {name}");
            await create.ExecuteNonQueryAsync();
            var builder = new NpgsqlConnectionStringBuilder(admin) { Database = name };
            var fixture = new TestDatabase(admin, name, builder.ConnectionString, container);
            Directory.CreateDirectory(fixture.Root);
            return fixture;
        }

        public async Task<PostgresDatabase> OpenAsync(string schema)
        {
            var database = new PostgresDatabase(connection, schema);
            await database.MigrateAsync();
            return database;
        }

        public async ValueTask DisposeAsync()
        {
            await using var source = NpgsqlDataSource.Create(adminConnection);
            await using var drop = source.CreateCommand($"DROP DATABASE {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
            if (container is not null) await container.DisposeAsync();
            Directory.Delete(Root, true);
        }
    }
}

