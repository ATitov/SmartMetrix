using Microsoft.Extensions.Options;

namespace SmartMetrix.CloudSyncService;

public sealed class CloudSyncWorker(
    SyncQueueStore queue,
    CloudSyncUploader uploader,
    IOptions<CloudSyncOptions> options,
    ILogger<CloudSyncWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Guid, long, Exception?> LogConflict =
        LoggerMessage.Define<Guid, long>(LogLevel.Error, new EventId(1801, "VersionConflict"),
            "Version conflict synchronizing {MeasurementId} version {Version}");
    private static readonly Action<ILogger, Guid, double, Exception?> LogRetry =
        LoggerMessage.Define<Guid, double>(LogLevel.Warning, new EventId(1802, "SyncRetry"),
            "Cloud unavailable; sync item {SyncItemId} will retry in {Delay}s");
    private readonly CloudSyncOptions options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var item = await queue.GetNextAsync(DateTimeOffset.UtcNow, stoppingToken);
            if (item is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
                continue;
            }

            item = item with { State = SyncState.Uploading, Attempts = item.Attempts + 1 };
            await queue.SaveAsync(item, "upload-started", null, stoppingToken);
            try
            {
                item = await uploader.UploadAsync(item,
                    checkpoint => queue.SaveAsync(checkpoint, "upload-checkpoint", null, stoppingToken), stoppingToken);
                item = item with { State = SyncState.Completed, CompletedAt = DateTimeOffset.UtcNow, LastError = null };
                await queue.SaveAsync(item, "cloud-acknowledged", null, stoppingToken);
                if (options.DeleteArtifactsAfterAcknowledgement) queue.DeleteSpool(item);
            }
            catch (CloudVersionConflictException exception)
            {
                item = item with { State = SyncState.Conflict, LastError = exception.Message };
                await queue.SaveAsync(item, "version-conflict", exception.Message, stoppingToken);
                LogConflict(logger, item.MeasurementId, item.Version, exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                var seconds = Math.Min(options.MaxBackoffSeconds, Math.Pow(2, Math.Min(item.Attempts, 20)));
                item = item with { State = SyncState.Retry, NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(seconds), LastError = exception.Message };
                await queue.SaveAsync(item, "upload-failed", exception.Message, stoppingToken);
                LogRetry(logger, item.Id, seconds, exception);
            }
        }
    }
}
