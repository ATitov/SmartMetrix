using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed class CloudDispatchOptions
{
    public bool Enabled { get; set; }
    [Required, Url] public string BaseUrl { get; set; } = "http://127.0.0.1:5213";
    [Range(1, 3600)] public int PollSeconds { get; set; } = 10;
    [Range(1, 3600)] public int TimeoutSeconds { get; set; } = 300;
}

// The completed measurement is the durable dispatch intent. A lost acknowledgement is
// retried with the same measurement/version; CloudSync owns durable deduplication.
public sealed class CloudResultDispatcher(IMeasurementStore store, HttpClient client,
    IOptions<CloudDispatchOptions> options, ILogger<CloudResultDispatcher> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogRetry = LoggerMessage.Define(LogLevel.Warning,
        new EventId(2201, "CloudDispatchRetry"), "Cloud queue dispatch failed; completed measurements remain pending.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DispatchPendingAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { LogRetry(logger, error); }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.PollSeconds), stoppingToken);
        }
    }

    public async Task DispatchPendingAsync(CancellationToken ct)
    {
        foreach (var item in await store.GetPendingCloudSyncAsync(ct))
        {
            try
            {
                using var response = await client.PostAsJsonAsync("v1/sync/measurements", new
                {
                    measurementId = item.Id,
                    version = item.Version,
                    runId = item.Pipeline!.RunId,
                    resultUri = item.Pipeline.ResultUri
                }, ct);
                response.EnsureSuccessStatusCode();
                await store.TrySaveAsync(item with { CloudQueuedAt = DateTimeOffset.UtcNow, Version = item.Version + 1 }, item.Version, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { LogRetry(logger, error); }
        }
    }
}
