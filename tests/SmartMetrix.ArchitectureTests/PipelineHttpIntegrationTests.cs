using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SmartMetrix.CalibrationService;
using SmartMetrix.LocalPositioningService;
using SmartMetrix.MeasurementOrchestrator;
using SmartMetrix.Domain;

namespace SmartMetrix.ArchitectureTests;

// Real service executables and HTTP serialization, with a local immutable artifact server instead of MinIO.
[Collection("ServiceProcesses")]
public sealed class PipelineHttpIntegrationTests
{
    private static readonly int[] ExpectedInstanceIds = [1, 2];
    [Fact]
    public async Task StoneVisionBackendCompletesTheHttpPipeline()
    {
        await using var rig = new PipelineTestRig { UseStoneVision = true };
        await rig.StartAsync();
        await rig.ConfigureRigAsync();
        var measurement = await rig.PostAsync<MeasurementProcess>("orchestrator", "measurements",
            new StartMeasurementRequest(Guid.NewGuid(), null, "EX-TEST", "quarry:test", "StoneVision integration"));
        var completed = await rig.WaitForTerminalAsync(measurement.Id);
        Assert.True(completed.Status == MeasurementStatus.Completed, JsonSerializer.Serialize(completed, PipelineJson.Options) + rig.Logs);
        var segmentation = await rig.GetAsync<JsonElement>("orchestrator", $"measurements/{measurement.Id}/stages/segmentation");
        Assert.False(segmentation.GetProperty("isTestData").GetBoolean());
        Assert.Equal("stonevision-http-test", segmentation.GetProperty("event").GetProperty("modelVersion").GetString());
        Assert.Equal(8192, segmentation.GetProperty("classPixelCounts").GetProperty("Rock").GetInt32());
        Assert.True(completed.IsTestData); // The camera simulator still marks the overall measurement.
        var raw = await rig.GetAsync<JsonElement>("storage", $"v1/measurements/{completed.Pipeline!.RunId}/artifacts/download/segmentation/stonevision.json");
        Assert.True(raw.GetProperty("success").GetBoolean());
        Assert.Equal(2, segmentation.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("A", segmentation.GetProperty("image").GetProperty("cameraId").GetString());
        Assert.Equal(2, segmentation.GetProperty("instances").GetArrayLength());
        var analysis = await rig.GetAsync<JsonElement>("orchestrator", $"measurements/{measurement.Id}/stages/analysis");
        Assert.NotEmpty(analysis.GetProperty("blocks").EnumerateArray());
        Assert.Equal(ExpectedInstanceIds, analysis.GetProperty("blocks").EnumerateArray()
            .Select(block => block.GetProperty("sourceInstanceId").GetInt32()).Distinct().Order());
        Assert.All(analysis.GetProperty("blocks").EnumerateArray(), block =>
            Assert.EndsWith("/segmentation/instances.json", block.GetProperty("sourceArtifacts").GetProperty("instanceMapUri").GetString()));
        var capabilities = await rig.GetAsync<JsonElement>("segmentation", "v1/segmentation/capabilities");
        Assert.True(capabilities.GetProperty("supportsInstances").GetBoolean());
        using var invalid = await rig.PostResponseAsync("segmentation", $"v1/measurements/{Guid.NewGuid()}/segmentation",
            new { frame = new { width = 1, height = 1, channels = 3, pixelFormat = "RGB8", pixels = new byte[3] }, detection = new { tileSize = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("InvalidSegmentationRequest", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        rig.StoneVisionInvalidResponse = true;
        using var badBackend = await rig.PostResponseAsync("segmentation", $"v1/measurements/{Guid.NewGuid()}/segmentation",
            new { frame = new { width = 128, height = 64, channels = 3, pixelFormat = "RGB8", pixels = new byte[128 * 64 * 3] } });
        Assert.Equal(HttpStatusCode.BadGateway, badBackend.StatusCode);
        Assert.Equal("InvalidSegmentationResponse", (await badBackend.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task RectificationMapsReachDepthAndKeepAnalysisOnOriginalCameraGrid()
    {
        await using var rig = new PipelineTestRig();
        await rig.StartAsync();
        await rig.ConfigureRigAsync(rectification: true);
        var measurement = await rig.PostAsync<MeasurementProcess>("orchestrator", "measurements",
            new StartMeasurementRequest(Guid.NewGuid(), null, "EX-TEST", "quarry:test", "rectification"));
        var result = await rig.WaitForTerminalAsync(measurement.Id);
        Assert.True(result.Status == MeasurementStatus.Completed, JsonSerializer.Serialize(result, PipelineJson.Options) + rig.Logs);
        var depth = await rig.GetAsync<JsonElement>("orchestrator", $"measurements/{result.Id}/stages/depth");
        Assert.Equal("CameraAOriginal", depth.GetProperty("pixelGrid").GetString());
        Assert.Equal(3, depth.GetProperty("rectificationChecksums").EnumerateObject().Count());
        Assert.True(depth.GetProperty("selectedBaselines").GetProperty("BC").GetInt32() > 0);
        Assert.True(result.D50 > 0);
        Assert.True(result.BlockCount > 0);
    }

    [Fact]
    [Trait("Requirement", "ORC-02")]
    public async Task ServicesCompleteMeasurementAndRejectBadFramesWithoutSyntheticResults()
    {
        await using var rig = new PipelineTestRig();
        await rig.StartAsync();
        await rig.ConfigureRigAsync();
        var commandId = Guid.NewGuid();
        var request = new StartMeasurementRequest(commandId, null, "EX-TEST", "quarry:test", "HTTP integration");
        rig.FailStageReads("quality", 1);
        var first = await rig.PostAsync<MeasurementProcess>("orchestrator", "measurements", request);
        var duplicate = await rig.PostAsync<MeasurementProcess>("orchestrator", "measurements", request);
        Assert.Equal(first.Id, duplicate.Id);
        var completed = await rig.WaitForTerminalAsync(first.Id);
        Assert.True(completed.Status == MeasurementStatus.Completed, JsonSerializer.Serialize(completed, PipelineJson.Options) + rig.Logs);
        Assert.True(completed.D50 > 0);
        Assert.True(completed.BlockCount > 0);
        Assert.True(completed.IsTestData);
        Assert.Null(completed.D20);
        Assert.Null(completed.D90);
        Assert.NotNull(completed.D95);
        Assert.Equal(9, completed.Pipeline!.Stages.Count);
        Assert.Equal(2, completed.Pipeline.Stages.Single(x => x.Name == "quality").Attempts);
        Assert.NotNull(completed.Pipeline.ResultUri);
        Assert.Contains(completed.Transitions, x => x.To == MeasurementStatus.Segmenting);
        Assert.Contains(completed.Transitions, x => x.To == MeasurementStatus.Persisting);
        var manifest = await rig.GetAsync<JsonElement>("orchestrator", $"measurements/{first.Id}/stages/result");
        Assert.Equal(completed.D50, manifest.GetProperty("analysis").GetProperty("d50Millimetres").GetDouble());
        var local = manifest.GetProperty("analysis").GetProperty("blocks").EnumerateArray().First(x => x.GetProperty("isValid").GetBoolean()).GetProperty("centre");
        var geo = manifest.GetProperty("georeference").GetProperty("blocks")[0].GetProperty("centre");
        Assert.InRange(geo.GetProperty("xMetres").GetDouble() - local.GetProperty("xMetres").GetDouble(), 10.99, 11.01);

        // Repeated delivery to CameraService returns the same durable receipt, including the timestamp.
        var capture = await rig.GetAsync<JsonElement>("orchestrator", $"measurements/{first.Id}/stages/capture");
        var repeated = await rig.PostAsync<JsonElement>("camera", $"v1/measurements/{completed.Pipeline.RunId}/capture",
            new { calibrationId = completed.Pipeline.CalibrationId });
        Assert.Equal(capture.GetProperty("response").GetRawText(), repeated.GetRawText());

        await rig.WriteFramesAsync(dark: true);
        var bad = await rig.PostAsync<MeasurementProcess>("orchestrator", "measurements", request with { CommandId = Guid.NewGuid() });
        var rejected = await rig.WaitForTerminalAsync(bad.Id);
        Assert.Equal(MeasurementStatus.Rejected, rejected.Status);
        Assert.Equal("QualityRejected", rejected.Pipeline!.ErrorCode);
        Assert.Null(rejected.D50);
        Assert.DoesNotContain(rejected.Pipeline.Stages, x => x.Name == "depth");

        // A user retry gets a new run namespace; the rejected attempt remains available in history.
        await rig.WriteFramesAsync(dark: false);
        await rig.PostAsync<MeasurementProcess>("orchestrator", $"measurements/{bad.Id}/retry",
            new WorkflowCommand(Guid.NewGuid(), rejected.Version, "Frames restored"));
        var retried = await rig.WaitForTerminalAsync(bad.Id);
        Assert.True(retried.Status == MeasurementStatus.Completed, retried.FailureReason + rig.Logs);
        Assert.NotEqual(rejected.Pipeline.RunId, retried.Pipeline!.RunId);
        Assert.Single(retried.PreviousRuns!);
    }

    [Fact]
    [Trait("Requirement", "ORC-03")]
    public async Task CancellationAndRestartPreserveCheckpointsAndRetriesAreBounded()
    {
        await using var rig = new PipelineTestRig();
        await rig.StartAsync();
        await rig.ConfigureRigAsync();
        var request = new StartMeasurementRequest(Guid.NewGuid(), null, "EX-TEST", "quarry:test", "Recovery integration");
        var block = rig.BlockStageRead("depth");
        var started = await rig.PostAsync<MeasurementProcess>("orchestrator", "measurements", request);
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var active = await rig.GetAsync<MeasurementProcess>("orchestrator", $"measurements/{started.Id}");
        await rig.PostAsync<MeasurementProcess>("orchestrator", $"measurements/{started.Id}/cancel",
            new WorkflowCommand(Guid.NewGuid(), active.Version, "Operator cancelled"));
        block.Release.TrySetResult();
        var cancelled = await rig.WaitForTerminalAsync(started.Id);
        Assert.Equal(MeasurementStatus.Rejected, cancelled.Status);
        Assert.Null(cancelled.D50);
        Assert.DoesNotContain(cancelled.Pipeline!.Stages, x => x.Name == "depth");

        block = rig.BlockStageRead("depth");
        var recovering = await rig.PostAsync<MeasurementProcess>("orchestrator", "measurements", request with { CommandId = Guid.NewGuid() });
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var before = await rig.GetAsync<MeasurementProcess>("orchestrator", $"measurements/{recovering.Id}");
        await rig.RestartOrchestratorAsync();
        block.Release.TrySetResult();
        var recovered = await rig.WaitForTerminalAsync(recovering.Id);
        Assert.True(recovered.Status == MeasurementStatus.Completed, recovered.FailureReason + rig.Logs);
        Assert.Equal(before.Pipeline!.RunId, recovered.Pipeline!.RunId);
        Assert.Equal(before.Pipeline.Stages.Single(x => x.Name == "capture"), recovered.Pipeline.Stages.Single(x => x.Name == "capture"));
        Assert.Equal(2, recovered.Pipeline.Stages.Single(x => x.Name == "depth").Attempts);

        block = rig.BlockStageRead("depth");
        var slow = await rig.PostAsync<MeasurementProcess>("orchestrator", "measurements", request with { CommandId = Guid.NewGuid() });
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var afterTimeout = await rig.WaitForTerminalAsync(slow.Id);
        Assert.True(afterTimeout.Status == MeasurementStatus.Completed, afterTimeout.FailureReason + rig.Logs);
        Assert.Equal(2, afterTimeout.Pipeline!.Stages.Single(x => x.Name == "depth").Attempts);
        Assert.Equal(MeasurementStatus.Rejected, (await rig.GetAsync<MeasurementProcess>("orchestrator", $"measurements/{started.Id}")).Status);

        rig.FailStageReads("calibration", 100);
        var failing = await rig.PostAsync<MeasurementProcess>("orchestrator", "measurements", request with { CommandId = Guid.NewGuid() });
        var failed = await rig.WaitForTerminalAsync(failing.Id);
        Assert.Equal(MeasurementStatus.Failed, failed.Status);
        Assert.Equal(3, failed.Pipeline!.ActiveAttempts);
        Assert.Equal("Http503", failed.Pipeline.ErrorCode);
        Assert.Empty(failed.Pipeline.Stages);
        Assert.Null(failed.D50);
    }
}

internal sealed class PipelineTestRig : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "smartmetrix-pipeline-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _urls = new(StringComparer.Ordinal);
    private readonly List<Process> _processes = [];
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly ConcurrentDictionary<string, byte[]> _artifacts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StageBlock> _blocks = new(StringComparer.Ordinal);
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private WebApplication? _storage;
    public string Logs => string.Join('\n', _logs.TakeLast(30));
    public bool UseStoneVision { get; init; }
    public string? PostgresConnection { get; init; }
    public bool StoneVisionInvalidResponse { get; set; }
    private static readonly string[] Services = ["CalibrationService", "CameraService", "QualityService", "DepthService", "SegmentationService", "BlockAnalysisService", "LocalPositioningService", "GeoreferenceService", "MeasurementOrchestrator"];
    private static readonly string[] Keys = ["calibration", "camera", "quality", "depth", "segmentation", "analysis", "positioning", "georeference", "orchestrator"];
    private static readonly string[] CameraIds = ["A", "B", "C"];
    private static readonly int[] StoneVisionMaskSize = [64, 128];
    // COCO runs are column-major: these two masks touch at the central column boundary.
    private static readonly int[] StoneVisionLeftCounts = [0, 4096, 4096];
    private static readonly int[] StoneVisionRightCounts = [4096, 4096];

    public async Task StartAsync()
    {
        Directory.CreateDirectory(_directory);
        await WriteFramesAsync(false);
        _urls["storage"] = FreeUrl();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(_urls["storage"]);
        builder.Logging.ClearProviders();
        _storage = builder.Build();
        if (UseStoneVision)
        {
            _storage.MapGet("/health", () => Results.Ok(new { models_loaded = true }));
            _storage.MapPost("/detect_json", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                var file = form.Files.GetFile("image");
                Assert.NotNull(file);
                using var image = new MemoryStream();
                await file.CopyToAsync(image);
                var header = System.Text.Encoding.ASCII.GetBytes("P6\n128 64\n255\n");
                Assert.Equal(header, image.ToArray()[..header.Length]);
                Assert.Equal(header.Length + 128 * 64 * 3, image.Length);
                if (StoneVisionInvalidResponse) return Results.Json(new { success = false });
                return Results.Json(new
                {
                    success = true,
                    coco_format = new
                    {
                        images = new[] { new { id = 1, width = 128, height = 64 } },
                        annotations = new[] {
                            new { id = 1, image_id = 1, category_id = 1, yolo_confidence = .9,
                                segmentation = new { size = StoneVisionMaskSize, counts = StoneVisionLeftCounts } },
                            new { id = 2, image_id = 1, category_id = 1, yolo_confidence = .9,
                                segmentation = new { size = StoneVisionMaskSize, counts = StoneVisionRightCounts } } }
                    }
                });
            });
        }
        _storage.MapPut("/v1/measurements/{id:guid}/artifacts/{**path}", async (Guid id, string path, HttpRequest request) =>
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            var key = $"{id}/{path}";
            var existing = _artifacts.GetOrAdd(key, bytes);
            return existing.AsSpan().SequenceEqual(bytes) ? Results.Ok(Metadata(id, path, existing)) : Results.Conflict();
        });
        _storage.MapGet("/v1/measurements/{id:guid}/artifacts/download/{**path}", async (Guid id, string path, CancellationToken ct) =>
        {
            if (_failures.TryGetValue(path, out var remaining) && remaining > 0)
            {
                _failures[path] = remaining - 1;
                return Results.StatusCode(503);
            }
            if (_blocks.TryRemove(path, out var block))
            {
                block.Entered.TrySetResult();
                await block.Release.Task.WaitAsync(ct);
            }
            return _artifacts.TryGetValue($"{id}/{path}", out var bytes) ? Results.Bytes(bytes, "application/octet-stream") : Results.NotFound();
        });
        _storage.MapGet("/v1/measurements/{id:guid}/artifacts/metadata/{**path}", (Guid id, string path) =>
            _artifacts.TryGetValue($"{id}/{path}", out var bytes) ? Results.Ok(Metadata(id, path, bytes)) : Results.NotFound());
        await _storage.StartAsync();
        foreach (var key in Keys) _urls[key] = FreeUrl();
        for (var i = 0; i < Services.Length; i++) StartService(Keys[i], Services[i]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        foreach (var key in Keys)
        {
            while (true)
            {
                try
                {
                    using var response = await _client.GetAsync(_urls[key] + "/health", timeout.Token);
                    if (response.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException) { }
                if (_processes.Any(x => x.HasExited)) throw new InvalidOperationException("Service exited: " + Logs);
                await Task.Delay(100, timeout.Token);
            }
        }
    }

    private void StartService(string key, string service)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SmartMetrix.sln"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Repository root was not found.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var assembly = Path.Combine(root.FullName, "src", "Services", "SmartMetrix." + service, "bin", configuration, "net10.0", "SmartMetrix." + service + ".dll");
        var contentRoot = Path.Combine(_directory, key);
        Directory.CreateDirectory(contentRoot);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = contentRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(assembly);
        start.Environment["ASPNETCORE_URLS"] = _urls[key];
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["Messaging__DispatchEnabled"] = "false";
        start.Environment["Persistence__Provider"] = PostgresConnection is null ? "File" : "Postgres";
        if (PostgresConnection is not null) start.Environment["ConnectionStrings__SmartMetrix"] = PostgresConnection;
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        start.Environment["Camera__Adapter"] = "Simulator";
        start.Environment["Camera__CameraASerialNumber"] = "A";
        start.Environment["Camera__CameraBSerialNumber"] = "B";
        start.Environment["Camera__CameraCSerialNumber"] = "C";
        start.Environment["Camera__SimulatorFrameDirectory"] = Path.Combine(_directory, "frames");
        start.Environment["Camera__StorageServiceUrl"] = _urls["storage"];
        start.Environment["Depth__StorageBaseUrl"] = _urls["storage"];
        start.Environment["Depth__Backend"] = "Cpu";
        start.Environment["Depth__CalibrationDirectory"] = Path.Combine(_directory, "calibration-maps");
        start.Environment["Depth__MaximumDisparity"] = "20";
        start.Environment["Depth__MinimumSpeckleSize"] = "4";
        start.Environment["Quality__Scenes__default__Version"] = "http-test-v1";
        start.Environment["Segmentation__StorageBaseUrl"] = _urls["storage"];
        start.Environment["Segmentation__Backend"] = UseStoneVision ? "StoneVision" : "Deterministic";
        if (UseStoneVision)
        {
            start.Environment["Segmentation__StoneVisionBaseUrl"] = _urls["storage"];
            start.Environment["Segmentation__ModelVersion"] = "stonevision-http-test";
        }
        start.Environment["Segmentation__InputWidth"] = "32";
        start.Environment["Segmentation__InputHeight"] = "32";
        start.Environment["Segmentation__TileOverlap"] = "4";
        start.Environment["Pipeline__Enabled"] = "true";
        start.Environment["Pipeline__RigId"] = "rig-test";
        start.Environment["Pipeline__RetryDelaySeconds"] = "1";
        start.Environment["Pipeline__RequestTimeoutSeconds"] = "2";
        start.Environment["MeasurementWorkflow__RunDemoPipeline"] = "false";
        foreach (var dependency in _urls.Where(x => x.Key != "orchestrator"))
            start.Environment[$"Pipeline__{char.ToUpperInvariant(dependency.Key[0])}{dependency.Key[1..]}Url"] = dependency.Value;
        var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) => { if (args.Data is { } line) _logs.Enqueue(key + ": " + line); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is { } line) _logs.Enqueue(key + ": " + line); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes.Add(process);
    }

    public async Task ConfigureRigAsync(bool rectification = false)
    {
        double[] identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        var payload = new CalibrationPayload("rig-test", CameraIds.Select(id => new CameraCalibration(id,
            new(80, 80, 64, 32, 128, 64), [0, 0, 0, 0, 0], identity, [0, 0, 0], "s3://calibration/identity")).ToArray(),
            new(.7, .8, 1.5), new(identity, [0, 0, 0]), .1);
        if (rectification)
        {
            var root = Path.Combine(_directory, "calibration-maps");
            var hashB = RectificationTests.WriteMaps(root, "AB.npz", 128, 64, 2);
            var hashC = RectificationTests.WriteMaps(root, "AC.npz", 128, 64, 3);
            var hashBc = RectificationTests.WriteMaps(root, "BC.npz", 128, 64, 1);
            payload = payload with
            {
                Rectification = new(2, 128, 64, "A", [
                new("B", "AB.npz", hashB, 80, 80, 62, 32, .7, identity, "A", [0, 0, 0]),
                new("C", "AC.npz", hashC, 80, 80, 61, 32, 1.5, identity, "A", [0, 0, 0]),
                new("C", "BC.npz", hashBc, 80, 80, 63, 32, .8, identity, "B", [.7, 0, 0])],
                new(80, 80, 64, 32, [0, 0, 0, 0, 0]))
            };
        }
        var record = await PostAsync<CalibrationRecord>("calibration", "api/calibrations/", payload);
        await PostAsync<CalibrationRecord>("calibration", $"api/calibrations/{record.Id}/activate",
            new ActivationRequest(DateTimeOffset.UtcNow.AddHours(-1), null, "test"));
        await PostAsync<TransformDefinition>("positioning", "api/transforms", new TransformDefinition("EX-TEST", "quarry:test", 1,
            new(new Vector3(10, 20, 30), Quaternion.Identity), DateTimeOffset.UtcNow.AddHours(-1)));
        using var response = await _client.PostAsJsonAsync(_urls["positioning"] + "/api/positioning/EX-TEST/samples",
            new[] { new PositioningSample(PositionSourceType.TotalStation, "test", 1_000_000_000, new Vector3(1, 2, 3), Quaternion.Identity, new double[36]) }, PipelineJson.Options);
        response.EnsureSuccessStatusCode();
    }

    public void FailStageReads(string stage, int count) => _failures[$"pipeline/{stage}.json"] = count;

    public StageBlock BlockStageRead(string stage)
    {
        var block = new StageBlock();
        _blocks[$"pipeline/{stage}.json"] = block;
        return block;
    }

    public async Task RestartOrchestratorAsync()
    {
        var process = _processes.Last();
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        _processes.Remove(process);
        process.Dispose();
        StartService("orchestrator", "MeasurementOrchestrator");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            try
            {
                using var response = await _client.GetAsync(_urls["orchestrator"] + "/health", timeout.Token);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100, timeout.Token);
        }
    }

    public async Task WriteFramesAsync(bool dark)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "frames"));
        var random = new Random(1701);
        var source = Enumerable.Range(0, 128 * 64).Select(_ => dark ? (byte)0 : (byte)random.Next(90, 230)).ToArray();
        for (var camera = 0; camera < 3; camera++)
        {
            var bytes = new byte[source.Length];
            for (var y = 0; y < 64; y++) for (var x = 0; x < 128; x++)
                    bytes[y * 128 + x] = x + camera * 6 < 128 ? source[y * 128 + x + camera * 6] : (byte)0;
            await File.WriteAllBytesAsync(Path.Combine(_directory, "frames", $"camera-{(char)('a' + camera)}.raw"), bytes);
        }
    }

    public Task<HttpResponseMessage> PostResponseAsync(string service, string path, object body) =>
        _client.PostAsJsonAsync(_urls[service] + "/" + path, body, PipelineJson.Options);

    public async Task<T> PostAsync<T>(string service, string path, object body)
    {
        using var response = await _client.PostAsJsonAsync(_urls[service] + "/" + path, body, PipelineJson.Options);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"{service}/{path}: {response.StatusCode}: {text}\n{Logs}");
        return JsonSerializer.Deserialize<T>(text, PipelineJson.Options)!;
    }

    public async Task<T> GetAsync<T>(string service, string path) =>
        (await _client.GetFromJsonAsync<T>(_urls[service] + "/" + path, PipelineJson.Options))!;

    public async Task<MeasurementProcess> WaitForTerminalAsync(Guid id)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(45))
        {
            var process = await GetAsync<MeasurementProcess>("orchestrator", $"measurements/{id}");
            if (process.Status is MeasurementStatus.Completed or MeasurementStatus.Rejected or MeasurementStatus.Failed) return process;
            await Task.Delay(100);
        }
        throw new TimeoutException("Pipeline did not finish. " + Logs);
    }

    private static object Metadata(Guid id, string path, byte[] bytes) => new
    {
        uri = $"s3://test/measurements/{id}/{path}",
        sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        measurementId = id,
        artifactPath = path,
        size = bytes.Length,
        contentType = "application/octet-stream"
    };

    private static string FreeUrl()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return $"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}";
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in _processes)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
        }
        if (_storage is not null) await _storage.DisposeAsync();
        _client.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    internal sealed class StageBlock
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
