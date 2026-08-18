namespace SmartMetrix.SegmentationService;

public enum MaskClass : byte { Background = 0, Rock = 1, Crack = 2 }

public sealed record SegmentationFrame(int Width, int Height, int Channels, string PixelFormat, byte[] Pixels);
public sealed record SegmentationRequest(SegmentationFrame Frame);
public sealed record SegmentationResult(
    SmartMetrix.Contracts.SegmentationCreated Event,
    Uri ConfidenceMapUri,
    bool LowConfidence,
    IReadOnlyDictionary<string, int> ClassPixelCounts);

public sealed class SegmentationOptions
{
    public const string SectionName = "Segmentation";
    public string Provider { get; set; } = "OnnxRuntime";
    public string Precision { get; set; } = "FP16";
    public string ModelPath { get; set; } = "models/rocks-v1.onnx";
    public string ModelVersion { get; set; } = "rocks-v1";
    public int InputWidth { get; set; } = 512;
    public int InputHeight { get; set; } = 512;
    public int TileOverlap { get; set; } = 64;
    public double MinimumConfidence { get; set; } = 0.6;
    public string StorageBaseUrl { get; set; } = "http://storage-service:8080";
}

public sealed record ModelDescriptor(string Version, int Width, int Height, int Channels, string PixelFormat,
    IReadOnlyList<MaskClass> Classes);
public sealed record TileInput(int Width, int Height, byte[] RgbPixels);
public sealed record TilePrediction(int Width, int Height, byte[] Classes, float[] Confidence);
