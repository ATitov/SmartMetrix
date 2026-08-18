using Microsoft.Extensions.Options;
using SmartMetrix.SegmentationService;

namespace SmartMetrix.ArchitectureTests;

public sealed class SegmentationGoldenTests
{
    [Fact]
    public async Task GoldenMaskIsDeterministicAndModelLoadsOnce()
    {
        var options = Options.Create(new SegmentationOptions { InputWidth = 4, InputHeight = 4, TileOverlap = 2, ModelVersion = "golden-v1" });
        var backend = new DeterministicSegmentationBackend(options); var store = new MemoryStore();
        var processor = new SegmentationProcessor(backend, store, options); var frame = GoldenFrame();
        var first = await processor.ProcessAsync(Guid.NewGuid(), new(frame), default);
        var second = await processor.ProcessAsync(Guid.NewGuid(), new(frame), default);

        Assert.Equal("golden-v1", first.Event.ModelVersion); Assert.Equal(1, backend.LoadCount); Assert.Equal(1, backend.WarmupCount);
        Assert.Equal(store.Payloads[0], store.Payloads[2]);
        Assert.Equal(new byte[] { 0, 0, 1, 1, 0, 0, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1 }, PgmPixels(store.Payloads[0]));
        Assert.Equal(first.Event.Confidence, second.Event.Confidence, 12);
    }

    [Fact]
    public async Task OverlappingTilesHaveNoBoundaryArtifactsAndLowConfidenceIsExplicit()
    {
        var options = Options.Create(new SegmentationOptions { InputWidth = 4, InputHeight = 4, TileOverlap = 2, MinimumConfidence = .9 });
        var store = new MemoryStore(); var processor = new SegmentationProcessor(new DeterministicSegmentationBackend(options), store, options);
        var pixels = Enumerable.Repeat((byte)80, 7 * 5 * 3).ToArray();
        var result = await processor.ProcessAsync(Guid.NewGuid(), new(new(7, 5, 3, "RGB8", pixels)), default);
        Assert.All(PgmPixels(store.Payloads[0]), value => Assert.Equal((byte)MaskClass.Rock, value));
        Assert.True(result.LowConfidence); Assert.True(result.Event.Confidence < .9);
        var origins = SegmentationProcessor.TileOrigins(2448, 2048, 512, 512, 64);
        Assert.Contains(origins, x => x.X == 1936); Assert.Contains(origins, x => x.Y == 1536);
    }

    [Fact]
    public async Task RejectsIncompatibleInputFormat()
    {
        var options = Options.Create(new SegmentationOptions { InputWidth = 2, InputHeight = 2 });
        var processor = new SegmentationProcessor(new DeterministicSegmentationBackend(options), new MemoryStore(), options);
        await Assert.ThrowsAsync<ArgumentException>(() => processor.ProcessAsync(Guid.NewGuid(), new(new(2, 2, 1, "GRAY8", new byte[4])), default));
    }

    private static SegmentationFrame GoldenFrame()
    {
        var values = new (byte R, byte G, byte B)[] { (0, 0, 0), (0, 0, 0), (100, 100, 100), (100, 100, 100), (0, 0, 0), (0, 0, 0), (100, 100, 100), (100, 100, 100), (200, 20, 20), (200, 20, 20), (100, 100, 100), (100, 100, 100), (200, 20, 20), (200, 20, 20), (100, 100, 100), (100, 100, 100) };
        return new(4, 4, 3, "RGB8", values.SelectMany(x => new[] { x.R, x.G, x.B }).ToArray());
    }
    private static byte[] PgmPixels(byte[] value) { var newlines = 0; var index = 0; for (; index < value.Length && newlines < 3; index++) if (value[index] == '\n') newlines++; return value[index..]; }
    private sealed class MemoryStore : ISegmentationArtifactStore
    {
        public List<byte[]> Payloads { get; } = [];
        public Task<Uri> PutAsync(Guid measurementId, string path, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) { Payloads.Add(content.ToArray()); return Task.FromResult(new Uri($"s3://golden/{measurementId:D}/{path}")); }
    }
}
