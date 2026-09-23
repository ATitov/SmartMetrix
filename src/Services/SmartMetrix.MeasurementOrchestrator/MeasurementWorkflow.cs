using Microsoft.Extensions.Options;
using SmartMetrix.Domain;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed class MeasurementWorkflowOptions
{
    public const string SectionName = "MeasurementWorkflow";
    public int StageTimeoutSeconds { get; set; } = 300;
    public int TimeoutScanSeconds { get; set; } = 5;
    public bool SeedDemoData { get; set; }
    public bool RunDemoPipeline { get; set; }
    public int DemoStageDelaySeconds { get; set; } = 4;
}

public sealed class MeasurementWorkflow(IMeasurementStore store, TimeProvider clock, IOptions<MeasurementWorkflowOptions> options)
{
    private TimeSpan StageTimeout => TimeSpan.FromSeconds(options.Value.StageTimeoutSeconds);

    public async Task<MeasurementProcess> StartAsync(Guid commandId, Guid? requestedId, string excavatorId,
        string coordinateSystemId, string? reason, CancellationToken cancellationToken)
    {
        if (commandId == Guid.Empty || string.IsNullOrWhiteSpace(excavatorId) || string.IsNullOrWhiteSpace(coordinateSystemId))
            throw new ArgumentException("CommandId, ExcavatorId and CoordinateSystemId are required.");
        // Stable identity also when a client retries a start command without MeasurementId.
        var id = requestedId ?? commandId;
        if (await store.GetAsync(id, cancellationToken) is { } existing)
        {
            if (existing.ProcessedCommands.Contains(commandId)) return existing;
            throw new InvalidOperationException($"Measurement {id} already exists.");
        }

        var now = clock.GetUtcNow();
        var requested = new MeasurementProcess(id, excavatorId, coordinateSystemId, MeasurementStatus.Requested, 0,
            now, now, now + StageTimeout, null, [], new HashSet<Guid>());
        var started = MeasurementStateMachine.Transition(requested, MeasurementStatus.Capturing, commandId, reason ?? "Start requested", now, StageTimeout);
        if (!await store.TryCreateAsync(started, cancellationToken))
        {
            var concurrent = await store.GetAsync(id, cancellationToken) ?? throw new InvalidOperationException();
            if (!concurrent.ProcessedCommands.Contains(commandId)) throw new InvalidOperationException($"Measurement {id} already exists.");
            return concurrent;
        }
        return started;
    }

    public Task<MeasurementProcess> CancelAsync(Guid id, Guid commandId, string? reason, long expectedVersion, CancellationToken ct) =>
        ApplyAsync(id, commandId, MeasurementStatus.Rejected, reason ?? "Cancelled", expectedVersion, ct);

    public Task<MeasurementProcess> RetryAsync(Guid id, Guid commandId, string? reason, long expectedVersion, CancellationToken ct) =>
        ApplyAsync(id, commandId, MeasurementStatus.Requested, reason ?? "Retry requested", expectedVersion, ct);

    public Task<MeasurementProcess> TransitionAsync(Guid id, Guid commandId, MeasurementStatus target, string? reason, long expectedVersion, CancellationToken ct) =>
        ApplyAsync(id, commandId, target, reason, expectedVersion, ct);

    public async Task TimeoutAsync(MeasurementProcess process, CancellationToken cancellationToken)
    {
        if (process.StageDeadline is null || process.StageDeadline > clock.GetUtcNow()) return;
        var commandId = DeterministicTimeoutCommand(process.Id, process.Version);
        try { await ApplyAsync(process.Id, commandId, MeasurementStatus.Failed, $"{process.Status} timed out", process.Version, cancellationToken); }
        catch (MeasurementConcurrencyException) { }
    }

    private async Task<MeasurementProcess> ApplyAsync(Guid id, Guid commandId, MeasurementStatus target,
        string? reason, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException($"Measurement {id} was not found.");
        if (current.ProcessedCommands.Contains(commandId)) return current;
        if (current.Version != expectedVersion) throw new MeasurementConcurrencyException(id, expectedVersion, current.Version);
        var changed = MeasurementStateMachine.Transition(current, target, commandId, reason, clock.GetUtcNow(), StageTimeout);
        if (target == MeasurementStatus.Requested)
            changed = changed with
            {
                PreviousRuns = current.Pipeline is null ? current.PreviousRuns : (current.PreviousRuns ?? []).Append(current.Pipeline).ToArray(),
                Pipeline = null,
                D10 = null,
                D20 = null,
                D50 = null,
                D80 = null,
                D90 = null,
                D95 = null,
                Confidence = null,
                BlockCount = null,
                OversizeFraction = null,
                Coverage = null,
                AlgorithmVersion = null,
                IsTestData = false
            };
        if (!await store.TrySaveAsync(changed, expectedVersion, cancellationToken))
        {
            var actual = await store.GetAsync(id, cancellationToken);
            throw new MeasurementConcurrencyException(id, expectedVersion, actual?.Version ?? -1);
        }
        return changed;
    }

    public async Task<MeasurementProcess> SavePipelineAsync(MeasurementProcess expected, PipelineProgress progress,
        MeasurementStatus target, string reason, CancellationToken ct,
        Func<MeasurementProcess, MeasurementProcess>? updateResult = null)
    {
        var now = clock.GetUtcNow();
        var changed = target == expected.Status
            ? expected with { Version = expected.Version + 1, UpdatedAt = now, StageDeadline = now + StageTimeout }
            : MeasurementStateMachine.Transition(expected, target, Guid.NewGuid(), reason, now, StageTimeout);
        changed = changed with { Pipeline = progress };
        if (updateResult is not null) changed = updateResult(changed);
        if (!await store.TrySaveAsync(changed, expected.Version, ct))
            throw new MeasurementConcurrencyException(expected.Id, expected.Version, (await store.GetAsync(expected.Id, ct))?.Version ?? -1);
        return changed;
    }

    private static Guid DeterministicTimeoutCommand(Guid id, long version)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        BitConverter.TryWriteBytes(bytes[8..], version);
        return new Guid(bytes);
    }
}

public sealed class MeasurementRecoveryService(IMeasurementStore store, MeasurementWorkflow workflow,
    IOptions<MeasurementWorkflowOptions> options, ILogger<MeasurementRecoveryService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Guid, Exception?> LogRecoveryFailure =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(1, nameof(LogRecoveryFailure)),
            "Failed to recover measurement {MeasurementId}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var process in await store.GetUnfinishedAsync(stoppingToken))
            {
                try { await workflow.TimeoutAsync(process, stoppingToken); }
                catch (Exception exception) { LogRecoveryFailure(logger, process.Id, exception); }
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.TimeoutScanSeconds), stoppingToken);
        }
    }
}
