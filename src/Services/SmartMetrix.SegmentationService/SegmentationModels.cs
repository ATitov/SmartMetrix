namespace SmartMetrix.SegmentationService;

public enum MaskClass : byte { Background = 0, Rock = 1, Crack = 2 }

public sealed record SegmentationFrame(int Width, int Height, int Channels, string PixelFormat, byte[] Pixels,
    string? CameraId = null, string? PixelGrid = null);
public sealed record SegmentationRequest(SegmentationFrame Frame, DetectionParameters? Detection = null);
public sealed record DetectionParameters(double YoloConfidence = .5, double YoloIou = .45, int YoloImageSize = 1920,
    int YoloMaxDetections = 1000, bool YoloTta = false, int MaskMinimumArea = 100, int TileSize = 0, double TileOverlap = .25);
public sealed record SegmentationImage(int Width, int Height, string PixelFormat, string? CameraId, string? PixelGrid);
public sealed record PixelBoundingBox(int X, int Y, int Width, int Height);
public sealed record StoneInstance(int InstanceId, PixelBoundingBox BoundingBox, int AreaPixels, Uri MaskUri,
    double YoloConfidence, double? SamScore);
public sealed record SegmentationProvenance(string Backend, string ModelVersion, string? BackendVersion, string? WeightsVersion);
public sealed record SegmentationWarning(string Code, string Message);
public sealed record SegmentationResult(
    SmartMetrix.Contracts.SegmentationCreated Event,
    Uri ConfidenceMapUri,
    bool LowConfidence,
    IReadOnlyDictionary<string, int> ClassPixelCounts,
    bool IsTestData = true)
{
    public int SchemaVersion { get; init; } = 2;
    public SegmentationImage? Image { get; init; }
    public SegmentationProvenance? Provenance { get; init; }
    public double ProcessingMilliseconds { get; init; }
    public DetectionParameters? Detection { get; init; }
    public IReadOnlyList<StoneInstance> Instances { get; init; } = [];
    public Uri? InstanceMapUri { get; init; }
    public Uri? RawResultUri { get; init; }
    public IReadOnlyList<SegmentationWarning> Warnings { get; init; } = [];
    public CrackInferenceProvenance? Cracks { get; init; }
}

public sealed class SegmentationOptions
{
    public const string SectionName = "Segmentation";
    public string Backend { get; set; } = "Deterministic";
    public string? CrackBaseUrl { get; set; }
    public string? CrackModelVersion { get; set; }
    public string? CrackWeightsSha256 { get; set; }
    public string StoneVisionBaseUrl { get; set; } = "http://127.0.0.1:5000";
    public int StoneVisionTimeoutSeconds { get; set; } = 180;
    public string? StoneVisionVersion { get; set; }
    public string? WeightsVersion { get; set; }
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
