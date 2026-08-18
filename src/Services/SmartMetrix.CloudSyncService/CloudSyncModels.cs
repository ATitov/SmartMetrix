using System.ComponentModel.DataAnnotations;

namespace SmartMetrix.CloudSyncService;

public sealed class CloudSyncOptions
{
    public const string SectionName = "CloudSync";

    [Required, Url] public string CloudBaseUrl { get; set; } = "http://cloud-api:8080";
    [Required] public string QueuePath { get; set; } = "data/cloud-sync";
    [Range(64 * 1024, 64 * 1024 * 1024)] public int ChunkSizeBytes { get; set; } = 1024 * 1024;
    [Range(0, long.MaxValue)] public long BandwidthLimitBytesPerSecond { get; set; } = 4 * 1024 * 1024;
    [Range(1, 3600)] public int PollIntervalSeconds { get; set; } = 5;
    [Range(1, 86400)] public int MaxBackoffSeconds { get; set; } = 300;
    public bool DeleteArtifactsAfterAcknowledgement { get; set; } = true;
}

public enum SyncPriority { RawFrame = 0, Result = 100 }
public enum SyncState { Pending, Uploading, Retry, Completed, Conflict }

public sealed record EnqueueSyncRequest(
    Guid MeasurementId,
    long Version,
    IReadOnlyDictionary<string, string>? Metadata,
    IReadOnlyList<EnqueueArtifact> Artifacts);

public sealed record EnqueueArtifact(string Path, string ContentType, SyncPriority Priority = SyncPriority.Result);

public sealed record SyncArtifact(
    string Name,
    string SpoolPath,
    string ContentType,
    string Sha256,
    long Size,
    SyncPriority Priority,
    long UploadedBytes = 0);

public sealed record SyncItem(
    Guid Id,
    Guid MeasurementId,
    long Version,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<SyncArtifact> Artifacts,
    SyncState State,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt,
    string? LastError = null,
    DateTimeOffset? CompletedAt = null);

public sealed record SyncQueueStatus(long PendingItems, long PendingBytes, DateTimeOffset? LastSuccessfulSync, long Conflicts);
public sealed record SyncAuditEntry(DateTimeOffset At, Guid ItemId, Guid MeasurementId, string Action, string? Detail);

public sealed class CloudVersionConflictException(string message) : InvalidOperationException(message);
public sealed class CloudChecksumException(string message) : IOException(message);
