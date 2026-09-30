using System.Net;
using Microsoft.Extensions.Options;
using SmartMetrix.CloudSyncService;

namespace SmartMetrix.ArchitectureTests;

public sealed class CloudSyncTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"smartmetrix-sync-{Guid.NewGuid():N}");

    [Fact]
    [Trait("Requirement", "SYN-01")]
    public async Task QueueIsDurablePrioritizedAndIdempotent()
    {
        Directory.CreateDirectory(root);
        var result = await WriteArtifactAsync("result.json", "confirmed result");
        var frame = await WriteArtifactAsync("frame.raw", "raw frame");
        var options = Options.Create(new CloudSyncOptions { QueuePath = Path.Combine(root, "queue") });
        var measurementId = Guid.NewGuid();
        var request = new EnqueueSyncRequest(measurementId, 1, new Dictionary<string, string> { ["site"] = "edge-1" },
        [
            new(frame, "application/octet-stream", SyncPriority.RawFrame),
            new(result, "application/json", SyncPriority.Result)
        ]);

        Guid itemId;
        using (var store = new SyncQueueStore(options))
        {
            var first = await store.EnqueueAsync(request, CancellationToken.None);
            var duplicate = await store.EnqueueAsync(request, CancellationToken.None);
            Assert.Equal(first.Id, duplicate.Id);
            Assert.Equal(SyncPriority.Result, first.Artifacts[0].Priority);
            Assert.All(first.Artifacts, artifact => Assert.True(File.Exists(artifact.SpoolPath)));
            itemId = first.Id;
        }

        using var reopened = new SyncQueueStore(options);
        var pending = await reopened.GetNextAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(itemId, pending?.Id);
        var status = await reopened.GetStatusAsync(CancellationToken.None);
        Assert.Equal(1, status.PendingItems);
        Assert.Equal("confirmed result".Length + "raw frame".Length, status.PendingBytes);
    }

    [Fact]
    [Trait("Requirement", "SYN-02")]
    public async Task UploaderResumesFromCheckpointAndSendsChecksums()
    {
        Directory.CreateDirectory(root);
        var path = await WriteArtifactAsync("result.bin", "abcdefghij");
        var storeOptions = Options.Create(new CloudSyncOptions { QueuePath = Path.Combine(root, "queue") });
        using var store = new SyncQueueStore(storeOptions);
        var item = await store.EnqueueAsync(new(Guid.NewGuid(), 3, null,
            [new(path, "application/octet-stream")]), CancellationToken.None);
        item = item with { Artifacts = [item.Artifacts[0] with { UploadedBytes = 4 }] };

        var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://cloud/") };
        var uploader = new CloudSyncUploader(client, Options.Create(new CloudSyncOptions
        {
            QueuePath = Path.Combine(root, "queue"),
            ChunkSizeBytes = 4,
            BandwidthLimitBytesPerSecond = 0
        }));
        var checkpoints = 0;
        var completed = await uploader.UploadAsync(item, _ => { checkpoints++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(10, completed.Artifacts[0].UploadedBytes);
        Assert.Equal(2, checkpoints);
        var chunks = handler.Requests.Where(x => x.Range is not null).ToArray();
        Assert.Equal(["bytes 4-7/10", "bytes 8-9/10"], chunks.Select(x => x.Range));
        Assert.All(chunks, x => Assert.False(string.IsNullOrWhiteSpace(x.ChunkChecksum)));
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    [Trait("Requirement", "SYN-04")]
    public async Task FailedEnqueueCanBeRetriedAfterMissingArtifactArrives()
    {
        Directory.CreateDirectory(root);
        var first = await WriteArtifactAsync("first.json", "result");
        var missing = Path.Combine(root, "later.raw");
        using var store = new SyncQueueStore(Options.Create(new CloudSyncOptions { QueuePath = Path.Combine(root, "queue") }));
        var request = new EnqueueSyncRequest(Guid.NewGuid(), 1, null,
            [new(first, "application/json"), new(missing, "application/octet-stream")]);

        await Assert.ThrowsAsync<FileNotFoundException>(() => store.EnqueueAsync(request, CancellationToken.None));
        Assert.Equal(0, (await store.GetStatusAsync(CancellationToken.None)).PendingItems);
        await File.WriteAllTextAsync(missing, "frame", CancellationToken.None);
        var accepted = await store.EnqueueAsync(request, CancellationToken.None);

        Assert.Equal(2, accepted.Artifacts.Count);
        Assert.Equal(1, (await store.GetStatusAsync(CancellationToken.None)).PendingItems);
    }

    [Theory]
    [Trait("Requirement", "SYN-03")]
    [InlineData(SyncState.Completed)]
    [InlineData(SyncState.Conflict)]
    public async Task TerminalItemsAreNotSelectedAfterReopeningQueue(SyncState state)
    {
        Directory.CreateDirectory(root);
        var source = await WriteArtifactAsync("result.json", "result");
        var options = Options.Create(new CloudSyncOptions { QueuePath = Path.Combine(root, "queue") });
        using (var store = new SyncQueueStore(options))
        {
            var item = await store.EnqueueAsync(new(Guid.NewGuid(), 1, null, [new(source, "application/json")]), CancellationToken.None);
            await store.SaveAsync(item with { State = state }, "test-terminal", null, CancellationToken.None);
        }
        using var reopened = new SyncQueueStore(options);
        Assert.Null(await reopened.GetNextAsync(DateTimeOffset.UtcNow.AddDays(1), CancellationToken.None));
        var status = await reopened.GetStatusAsync(CancellationToken.None);
        Assert.Equal(0, status.PendingItems);
        Assert.Equal(state == SyncState.Conflict ? 1 : 0, status.Conflicts);
    }

    [Fact]
    [Trait("Requirement", "SYN-02")]
    public async Task CorruptedSpoolIsNeverUploadedOrCheckpointed()
    {
        Directory.CreateDirectory(root);
        var source = await WriteArtifactAsync("result.json", "result");
        using var store = new SyncQueueStore(Options.Create(new CloudSyncOptions { QueuePath = Path.Combine(root, "queue") }));
        var item = await store.EnqueueAsync(new(Guid.NewGuid(), 1, null, [new(source, "application/json")]), CancellationToken.None);
        await File.WriteAllTextAsync(item.Artifacts[0].SpoolPath, "corrupt", CancellationToken.None);
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://cloud/") };
        var uploader = new CloudSyncUploader(client, Options.Create(new CloudSyncOptions()));
        var checkpoints = 0;
        await Assert.ThrowsAsync<CloudChecksumException>(() => uploader.UploadAsync(item,
            _ => { checkpoints++; return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(0, checkpoints);
        Assert.Null(Assert.Single(handler.Requests).Range);
    }

    private async Task<string> WriteArtifactAsync(string name, string content)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllTextAsync(path, content, CancellationToken.None);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string? Range, string? ChunkChecksum)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Content?.Headers.ContentRange?.ToString(),
                request.Headers.TryGetValues("X-Chunk-SHA256", out var values) ? values.Single() : null));
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (Requests[^1].ChunkChecksum is { } checksum) response.Headers.TryAddWithoutValidation("X-Chunk-SHA256", checksum);
            return Task.FromResult(response);
        }
    }
}
