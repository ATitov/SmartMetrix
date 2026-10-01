namespace SmartMetrix.QualityService;

public sealed class QualityOptions
{
    public static readonly string[] EditableThresholds = ["MinimumSharpness", "MinimumExposure", "MaximumSaturation", "MinimumTexture", "MaximumShadowFraction", "MaximumLensContamination", "MaximumTimestampSkewNanoseconds"];
    public static readonly string[] EditableMetrics = ["Metrics:ContaminationStandardDeviationScale", "Metrics:SharpnessNormalization", "Metrics:TextureScale", "Metrics:SaturationLow", "Metrics:SaturationHigh", "Metrics:ShadowThreshold"];
    public const string SectionName = "Quality";
    public string DefaultSceneType { get; set; } = "default";
    public Dictionary<string, QualityThresholds> Scenes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public QualityMetricProfile Metrics { get; set; } = new();
    public string ResultDirectory { get; set; } = "data/quality-results";

    public bool IsValid() => !string.IsNullOrWhiteSpace(ResultDirectory) && Metrics.IsValid() &&
        Scenes.ContainsKey(DefaultSceneType) && Scenes.Values.All(x => x.IsValid());
}

public sealed class QualityMetricProfile
{
    public string Version { get; set; } = "heuristic-unvalidated-v1";
    public double ContaminationStandardDeviationScale { get; set; } = 48;
    public double SharpnessNormalization { get; set; } = 16_384;
    public double TextureScale { get; set; } = 4;
    public int SaturationLow { get; set; } = 5;
    public int SaturationHigh { get; set; } = 250;
    public int ShadowThreshold { get; set; } = 38;
    public bool FieldValidated { get; set; }
    public bool IsValid() => !string.IsNullOrWhiteSpace(Version) &&
        double.IsFinite(ContaminationStandardDeviationScale) && ContaminationStandardDeviationScale > 0 &&
        double.IsFinite(SharpnessNormalization) && SharpnessNormalization > 0 &&
        double.IsFinite(TextureScale) && TextureScale > 0 && SaturationLow >= 0 &&
        SaturationHigh <= 255 && SaturationLow < SaturationHigh && ShadowThreshold is >= 0 and <= 255;
}

public sealed class QualityThresholds
{
    public string Version { get; set; } = "1";
    public double MinimumSharpness { get; set; } = 0.15;
    public double MinimumExposure { get; set; } = 0.35;
    public double MaximumSaturation { get; set; } = 0.15;
    public double MinimumTexture { get; set; } = 0.08;
    public double MaximumShadowFraction { get; set; } = 0.45;
    public double MaximumLensContamination { get; set; } = 0.6;
    public long MaximumTimestampSkewNanoseconds { get; set; } = 5_000_000;
    public bool IsValid() => !string.IsNullOrWhiteSpace(Version) && MaximumTimestampSkewNanoseconds >= 0 &&
        new[] { MinimumSharpness, MinimumExposure, MaximumSaturation, MinimumTexture,
            MaximumShadowFraction, MaximumLensContamination }.All(x => double.IsFinite(x) && x is >= 0 and <= 1);
}
