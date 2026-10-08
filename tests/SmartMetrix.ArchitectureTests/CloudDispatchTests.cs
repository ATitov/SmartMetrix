using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartMetrix.CloudSyncService;
using SmartMetrix.Domain;
using SmartMetrix.MeasurementOrchestrator;

namespace SmartMetrix.ArchitectureTests;

public sealed class CloudDispatchTests
{
    [Fact]
    public async Task LostAcknowledgementAndRestartKeepOneQueueItemAndAllRunArtifacts()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cloud-dispatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = folder });
            await using var app = builder.Build();
            var store = new JsonMeasurementStore(app.Environment);
            var id = Guid.NewGuid(); var run = Guid.NewGuid();
            var root = $"s3://artifacts/measurements/{run:D}/";
            var now = DateTimeOffset.UtcNow;
            var process = new MeasurementProcess(id, "exc", "quarry", MeasurementStatus.Completed, 20,
                now, now, null, null, [], [], Pipeline: new(run, "Services", [], ResultUri: root + "pipeline/result.json"));
            Assert.True(await store.TryCreateAsync(process));
            var documents = new Dictionary<string, string>
            {
                ["pipeline/result.json"] = JsonSerializer.Serialize(new
                {
                    measurementId = id,
                    processingRunId = run,
                    stage = root + "pipeline/capture.json",
                    cloud = root + "depth/cloud.ply"
                }),
                ["pipeline/capture.json"] = JsonSerializer.Serialize(new { frame = root + "frames/A.pgm", repeated = root + "depth/cloud.ply" }),
                ["depth/cloud.ply"] = "ply data",
                ["frames/A.pgm"] = "frame data"
            };
            using var storage = new HttpClient(new StorageHandler(documents)) { BaseAddress = new("http://storage/") };
            var options = Options.Create(new CloudSyncOptions { QueuePath = Path.Combine(folder, "queue") });
            using var queue = new SyncQueueStore(options);
            var importer = new MeasurementArtifactImporter(storage, queue, options);
            using var delivery = new HttpClient(new LostAckHandler(importer)) { BaseAddress = new("http://sync/") };
            documents.Remove("frames/A.pgm");
            using (var dispatcher = Dispatcher(store, delivery)) await dispatcher.DispatchPendingAsync(default);
            Assert.Equal(0, (await queue.GetStatusAsync(default)).PendingItems);
            Assert.Null((await store.GetAsync(id))!.CloudQueuedAt);
            documents["frames/A.pgm"] = "frame data";
            using (var dispatcher = Dispatcher(store, delivery)) await dispatcher.DispatchPendingAsync(default);
            Assert.Null((await store.GetAsync(id))!.CloudQueuedAt);
            Assert.Equal(MeasurementStatus.Completed, (await store.GetAsync(id))!.Status);
            Assert.Equal(1, (await queue.GetStatusAsync(default)).PendingItems);

            // No source is available on retry: the durable queue acknowledgement must suffice.
            documents.Clear();
            var reopened = new JsonMeasurementStore(app.Environment);
            using (var dispatcher = Dispatcher(reopened, delivery)) await dispatcher.DispatchPendingAsync(default);
            Assert.NotNull((await reopened.GetAsync(id))!.CloudQueuedAt);
            Assert.Empty(await reopened.GetPendingCloudSyncAsync());
            Assert.Equal(1, (await queue.GetStatusAsync(default)).PendingItems);
            var queued = await queue.FindAsync(id, 20, default);
            Assert.NotNull(queued);
            Assert.Equal(5, queued.Artifacts.Count); // result, stage, cloud, frame, URI index
            Assert.Single(queued.Artifacts, x => x.Priority == SyncPriority.RawFrame);
            Assert.All(queued.Artifacts, x => Assert.True(File.Exists(x.SpoolPath)));
            using var restored = new SyncQueueStore(options);
            Assert.Equal(queued.Id, (await restored.FindAsync(id, 20, default))!.Id);
            await Assert.ThrowsAsync<ArgumentException>(() => importer.EnqueueAsync(
                new(id, 20, run, "https://untrusted/result.json"), default));
        }
        finally { Directory.Delete(folder, true); }
    }

    private static CloudResultDispatcher Dispatcher(IMeasurementStore store, HttpClient client) =>
        new(store, client, Options.Create(new CloudDispatchOptions()), NullLogger<CloudResultDispatcher>.Instance);

    private sealed class StorageHandler(Dictionary<string, string> documents) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Split("/artifacts/download/", StringSplitOptions.None)[1];
            return Task.FromResult(documents.TryGetValue(path, out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class LostAckHandler(MeasurementArtifactImporter importer) : HttpMessageHandler
    {
        private bool first = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadFromJsonAsync<EnqueueMeasurementRequest>(cancellationToken);
            await importer.EnqueueAsync(body!, cancellationToken);
            if (first) { first = false; throw new HttpRequestException("Acknowledgement lost after durable enqueue."); }
            return new(HttpStatusCode.Accepted);
        }
    }
}
