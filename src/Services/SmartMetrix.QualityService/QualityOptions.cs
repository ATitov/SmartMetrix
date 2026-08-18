namespace SmartMetrix.QualityService;

public sealed class QualityOptions
{
    public const string SectionName = "Quality";
    public string DefaultSceneType { get; set; } = "default";
    public Dictionary<string, QualityThresholds> Scenes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
}
