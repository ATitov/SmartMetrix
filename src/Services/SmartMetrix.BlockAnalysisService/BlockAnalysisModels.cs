using System.ComponentModel.DataAnnotations;
using SmartMetrix.Domain;

namespace SmartMetrix.BlockAnalysisService;

public enum AnalysisMaskClass : byte { Background = 0, Rock = 1, Crack = 2 }

public readonly record struct OrganizedPoint(double XMetres, double YMetres, double ZMetres, double DepthConfidence)
{
    public bool IsValid => double.IsFinite(XMetres) && double.IsFinite(YMetres) && double.IsFinite(ZMetres) && DepthConfidence > 0;
}

public sealed record AnalysisArtifactReferences(Uri PointCloudUri, Uri MaskUri, Uri DepthConfidenceUri,
    Uri SegmentationConfidenceUri, string CalibrationId);
public sealed record BlockAnalysisRequest(int Width, int Height, IReadOnlyList<OrganizedPoint> Points,
    IReadOnlyList<byte> Mask, IReadOnlyList<float> SegmentationConfidence, double CalibrationConfidence,
    string CoordinateSystemId, AnalysisArtifactReferences Artifacts);

public sealed record AlgorithmProvenance(string Name, string Version, IReadOnlyDictionary<string, double> Parameters);
public sealed record BlockGeometry(double MajorAxisMillimetres, double IntermediateAxisMillimetres,
    double MinorAxisMillimetres, double EquivalentDiameterMillimetres, double ProjectedAreaSquareMillimetres,
    double VolumeCubicMillimetres);
public sealed record AnalysedBlock(Guid BlockId, Guid MeasurementId, string CoordinateSystemId, LocalPoint Centre,
    BlockGeometry Geometry, bool IsValid, bool IsPartiallyVisible, double Confidence,
    IReadOnlyList<string> QualityReasons, AnalysisArtifactReferences SourceArtifacts);
public sealed record BlockAnalysisResult(Guid MeasurementId, string LengthUnit, string AreaUnit, string VolumeUnit,
    IReadOnlyList<AnalysedBlock> Blocks, IReadOnlyDictionary<string, int> SizeClasses,
    double D10Millimetres, double D50Millimetres, double D80Millimetres, double D95Millimetres,
    int OversizeCount, double Confidence, IReadOnlyList<string> QualityReasons, AlgorithmProvenance Provenance);

public static class BlockQualityReasons
{
    public const string TooFewPoints = "too-few-points";
    public const string PartialVisibility = "partial-visibility";
    public const string LowDepthConfidence = "low-depth-confidence";
    public const string LowSegmentationConfidence = "low-segmentation-confidence";
    public const string LowCalibrationConfidence = "low-calibration-confidence";
    public const string OutliersRemoved = "outliers-removed";
    public const string NoValidBlocks = "no-valid-blocks";
}

public sealed class BlockAnalysisOptions
{
    public const string SectionName = "BlockAnalysis";
    [Range(1, int.MaxValue)] public int MinimumPointsPerBlock { get; set; } = 8;
    [Range(0.000001, double.MaxValue)] public double MaximumNeighbourDistanceMetres { get; set; } = .15;
    [Range(1, double.MaxValue)] public double OutlierDistanceFactor { get; set; } = 3;
    [Range(0, int.MaxValue)] public int PartialVisibilityBorderPixels { get; set; } = 1;
    [Range(0, double.MaxValue)] public double OversizeThresholdMillimetres { get; set; } = 1000;
    [Required] public string AlgorithmVersion { get; set; } = "connected-components-pca-v1";
}
