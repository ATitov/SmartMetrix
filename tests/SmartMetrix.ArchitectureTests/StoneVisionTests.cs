using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartMetrix.SegmentationService;

namespace SmartMetrix.ArchitectureTests;

public sealed class StoneVisionTests
{
    private static readonly JsonSerializerOptions CrackJson = new(JsonSerializerDefaults.Web);
    private static readonly int[] MaskSize = [2, 3];
    private static readonly int[] ExpectedLabels = [1, 0, 1, 0, 1, 0];
    private static readonly int[] ExpectedRuns = [0, 1, 2, 2, 1];
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrackInferenceMergesValidatedPixelsOrRejectsDifferentGrid(bool wrongGrid)
    {
        var store = new Store();
        var options = Options.Create(new SegmentationOptions
        {
            ModelVersion = "stonevision-test",
            CrackBaseUrl = "http://cracks/",
            CrackModelVersion = "cracks-v1",
            CrackWeightsSha256 = new string('a', 64)
        });
        var frame = Frame();
        var prediction = new CrackPrediction(1, 3, 2, "A", wrongGrid ? "OtherGrid" : "CameraAOriginal",
            [1, 0, 0, 0, 0, 0], [230, 0, 0, 0, 0, 0], "cracks-v1", new string('a', 64));
        using var stoneHttp = new HttpClient(new Handler(Response([0, 1, 2, 2, 1]))) { BaseAddress = new("http://stonevision/") };
        using var crackHttp = new HttpClient(new Handler(JsonSerializer.Serialize(prediction, CrackJson))) { BaseAddress = new("http://cracks/") };
        var processor = new StoneVisionProcessor(new(stoneHttp), store, options, new(crackHttp, options));
        if (wrongGrid)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(Guid.NewGuid(), frame, default));
            Assert.Empty(store.Items);
            return;
        }
        var result = await processor.ProcessAsync(Guid.NewGuid(), frame, default);
        Assert.Equal(1, result.ClassPixelCounts["Crack"]);
        Assert.Equal(2, result.ClassPixelCounts["Rock"]);
        Assert.Equal(new byte[] { 2, 0, 1, 0, 1, 0 }, Pixels(store.Items["segmentation/mask.pgm"]));
        Assert.Equal("cracks-v1", result.Cracks!.ModelVersion);
        Assert.Equal(new string('a', 64), result.Cracks.WeightsSha256);
        var labels = JsonSerializer.Deserialize<JsonElement>(store.Items["segmentation/instances.json"]).GetProperty("labels");
        Assert.Equal(0, labels[0].GetInt32());
        Assert.Equal(1, labels[2].GetInt32());
        Assert.Contains("segmentation/cracks.json", store.Items.Keys);
    }
    [Fact]
    public async Task ConvertsColumnMajorMasksAndPreservesOriginalResponse()
    {
        var handler = new Handler(Response([0, 1, 2, 2, 1]));
        var store = new Store();
        var result = await Processor(handler, store).ProcessAsync(Guid.NewGuid(), Frame(), default);
        Assert.Equal(new byte[] { 1, 0, 1, 0, 1, 0 }, Pixels(store.Items["segmentation/mask.pgm"]));
        Assert.Equal(new byte[] { 204, 0, 204, 0, 204, 0 }, Pixels(store.Items["segmentation/confidence.pgm"]));
        Assert.Equal(3, result.ClassPixelCounts["Rock"]);
        Assert.Equal(0, result.ClassPixelCounts["Crack"]);
        Assert.False(result.IsTestData);
        Assert.True(result.LowConfidence);
        Assert.Equal("stonevision-test", result.Event.ModelVersion);
        Assert.Contains("coco_format", Encoding.UTF8.GetString(store.Items["segmentation/stonevision.json"]));
        Assert.Equal("http://stonevision/detect_json", handler.Url);
        Assert.Equal("image/x-portable-pixmap", handler.ImageType);
        Assert.Equal(Encoding.ASCII.GetBytes("P6\n3 2\n255\n").Concat(Frame().Frame.Pixels), handler.Image);
        var instance = Assert.Single(result.Instances);
        Assert.Equal(1, instance.InstanceId);
        Assert.Equal(3, instance.AreaPixels);
        Assert.Equal(new PixelBoundingBox(0, 0, 3, 2), instance.BoundingBox);
        Assert.Equal(.8, instance.YoloConfidence);
        Assert.Equal(.9, instance.SamScore);
        Assert.Equal("StoneVision", result.Provenance!.Backend);
        Assert.Equal("CameraAOriginal", result.Image!.PixelGrid);
        Assert.Equal(new DetectionParameters(), result.Detection);
        Assert.Equal("0.5", handler.Parameters["yolo_conf"]);
        var map = JsonSerializer.Deserialize<JsonElement>(store.Items["segmentation/instances.json"]);
        Assert.Equal(ExpectedLabels, map.GetProperty("labels").EnumerateArray().Select(x => x.GetInt32()));
        var rle = JsonSerializer.Deserialize<JsonElement>(store.Items["segmentation/instances/1.json"]);
        Assert.Equal(ExpectedRuns, rle.GetProperty("counts").EnumerateArray().Select(x => x.GetInt32()));
    }

    [Theory]
    [InlineData("[0,7]")]
    [InlineData("[0,-1,7]")]
    [InlineData("[0,1]")]
    [InlineData("\"compressed-rle\"")]
    [Trait("Requirement", "SEG-01")]
    public async Task RejectsInvalidRleWithoutWritingArtifacts(string counts)
    {
        var response = Response([0, 1, 2, 2, 1]).Replace("[0,1,2,2,1]", counts, StringComparison.Ordinal);
        var store = new Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => Processor(new Handler(response), store).ProcessAsync(Guid.NewGuid(), Frame(), default));
        Assert.Empty(store.Items);
    }

    [Theory]
    [InlineData("\"size\":[2,3]", "\"size\":[3,2]")]
    [InlineData("\"width\":3", "\"width\":4")]
    [InlineData("\"segmentation\":", "\"missing_mask\":")]
    [InlineData("\"yolo_confidence\":0.8", "\"yolo_confidence\":1.8")]
    [InlineData("\"success\":true", "\"success\":false")]
    [InlineData("\"sam_score\":0.9", "\"sam_score\":-1")]
    public async Task RejectsIncompatibleResponses(string original, string replacement)
    {
        var store = new Store();
        var response = Response([0, 6]).Replace(original, replacement, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => Processor(new Handler(response), store).ProcessAsync(Guid.NewGuid(), Frame(), default));
        Assert.Empty(store.Items);
    }

    [Fact]
    public async Task EmptyDetectionsHaveUnknownConfidenceAndNoRockPixels()
    {
        var response = "{\"success\":true,\"coco_format\":{\"images\":[{\"id\":1,\"width\":3,\"height\":2}],\"annotations\":[]}}";
        var store = new Store();
        var result = await Processor(new Handler(response), store).ProcessAsync(Guid.NewGuid(), Frame(), default);
        Assert.Equal(0, result.Event.Confidence);
        Assert.Equal(0, result.ClassPixelCounts["Rock"]);
        Assert.True(result.LowConfidence);
        Assert.Empty(result.Instances);
        Assert.Contains(result.Warnings, warning => warning.Code == "NoDetections");
    }

    [Fact]
    public async Task ForwardsCustomParametersAndRejectsInvalidValuesBeforeCallingBackend()
    {
        var handler = new Handler(Response([0, 6]));
        var parameters = new DetectionParameters(.65, .3, 640, 20, true, 10, 256, .4);
        var result = await Processor(handler, new Store()).ProcessAsync(Guid.NewGuid(), Frame() with { Detection = parameters }, default);
        Assert.Equal(parameters, result.Detection);
        Assert.Equal("0.65", handler.Parameters["yolo_conf"]);
        Assert.Equal("0.3", handler.Parameters["yolo_iou"]);
        Assert.Equal("640", handler.Parameters["yolo_imgsz"]);
        Assert.Equal("20", handler.Parameters["yolo_max_det"]);
        Assert.Equal("true", handler.Parameters["yolo_tta"]);
        Assert.Equal("10", handler.Parameters["mask_min_area"]);
        Assert.Equal("256", handler.Parameters["tile_size"]);
        Assert.Equal("0.4", handler.Parameters["tile_overlap"]);
        foreach (var invalid in new[] { parameters with { YoloConfidence = double.NaN }, parameters with { TileSize = 1 },
            parameters with { YoloImageSize = 33 }, parameters with { TileOverlap = .6 }, parameters with { YoloMaxDetections = 0 } })
        {
            var unused = new Handler("{}");
            await Assert.ThrowsAsync<ArgumentException>(() => Processor(unused, new Store()).ProcessAsync(Guid.NewGuid(), Frame() with { Detection = invalid }, default));
            Assert.Null(unused.Url);
        }
    }

    [Fact]
    [Trait("Requirement", "SEG-02")]
    public async Task OverlapUsesHighestConfidenceThenLowerIdAndRetainsBothOriginalMasks()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(Response([0, 6]))!;
        var annotations = root["coco_format"]!["annotations"]!.AsArray();
        var second = annotations[0]!.DeepClone();
        annotations[0]!["id"] = 2;
        second["id"] = 1;
        annotations.Add(second);
        var store = new Store();
        var result = await Processor(new Handler(root.ToJsonString()), store).ProcessAsync(Guid.NewGuid(), Frame(), default);
        Assert.Equal(2, result.Instances.Count);
        Assert.Contains(result.Warnings, warning => warning.Code == "OverlappingInstances");
        Assert.All(JsonSerializer.Deserialize<JsonElement>(store.Items["segmentation/instances.json"]).GetProperty("labels").EnumerateArray(), x => Assert.Equal(1, x.GetInt32()));
        annotations[0]!["yolo_confidence"] = .95;
        var strongerStore = new Store();
        await Processor(new Handler(root.ToJsonString()), strongerStore).ProcessAsync(Guid.NewGuid(), Frame(), default);
        Assert.All(JsonSerializer.Deserialize<JsonElement>(strongerStore.Items["segmentation/instances.json"]).GetProperty("labels").EnumerateArray(), x => Assert.Equal(2, x.GetInt32()));
        second["id"] = 2;
        var invalidStore = new Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => Processor(new Handler(root.ToJsonString()), invalidStore).ProcessAsync(Guid.NewGuid(), Frame(), default));
        Assert.Empty(invalidStore.Items);
    }

    [Fact]
    [Trait("Requirement", "SEG-03")]
    public async Task HttpFailureDoesNotFallBackToSimulator()
    {
        var store = new Store();
        await Assert.ThrowsAsync<HttpRequestException>(() => Processor(new Handler("{}", HttpStatusCode.ServiceUnavailable), store)
            .ProcessAsync(Guid.NewGuid(), Frame(), default));
        Assert.Empty(store.Items);
    }

    [Theory]
    [InlineData("{\"models_loaded\":false}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"models_loaded\":\"true\"}")]
    [InlineData("not-json")]
    [Trait("Requirement", "SEG-03")]
    public async Task ReadinessRequiresLoadedModels(string response)
    {
        using var http = new HttpClient(new Handler(response)) { BaseAddress = new("http://stonevision/") };
        var health = new StoneVisionHealthCheck(new StoneVisionClient(http));
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, (await health.CheckHealthAsync(new())).Status);
    }

    private static string Response(int[] counts) => JsonSerializer.Serialize(new
    {
        success = true,
        coco_format = new
        {
            images = new[] { new { id = 1, width = 3, height = 2 } },
            annotations = new[] { new { id = 1, image_id = 1, category_id = 1, yolo_confidence = .8, sam_score = .9, segmentation = new { size = MaskSize, counts } } }
        }
    });
    private static SegmentationRequest Frame() => new(new(3, 2, 3, "RGB8", Enumerable.Range(0, 18).Select(x => (byte)x).ToArray(), "A", "CameraAOriginal"));
    private static StoneVisionProcessor Processor(Handler handler, Store store) => new(
        new StoneVisionClient(new HttpClient(handler) { BaseAddress = new("http://stonevision/") }), store,
        Options.Create(new SegmentationOptions { ModelVersion = "stonevision-test" }));
    private static byte[] Pixels(byte[] image)
    {
        var offset = 0;
        for (var lines = 0; lines < 3; offset++) if (image[offset] == '\n') lines++;
        return image[offset..];
    }
    private sealed class Store : ISegmentationArtifactStore
    {
        public Dictionary<string, byte[]> Items { get; } = [];
        public Task<Uri> PutAsync(Guid measurementId, string path, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            Items.Add(path, content.ToArray());
            return Task.FromResult(new Uri($"s3://test/{measurementId}/{path}"));
        }
    }
    private sealed class Handler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? ImageType { get; private set; }
        public byte[]? Image { get; private set; }
        public Dictionary<string, string> Parameters { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.ToString();
            if (request.Content is MultipartFormDataContent multipart)
            {
                var part = Assert.Single(multipart, item => item.Headers.ContentDisposition?.FileName is not null);
                ImageType = part.Headers.ContentType!.MediaType;
                Image = await part.ReadAsByteArrayAsync(cancellationToken);
                foreach (var parameter in multipart.Where(item => item.Headers.ContentDisposition?.FileName is null))
                    Parameters.Add(parameter.Headers.ContentDisposition!.Name!.Trim('"'), await parameter.ReadAsStringAsync(cancellationToken));
            }
            return new(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
