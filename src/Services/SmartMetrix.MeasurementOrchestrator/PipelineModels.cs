using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";
    public bool Enabled { get; set; } = true;
    public string RigId { get; set; } = "";
    public string SceneType { get; set; } = "default";
    public bool FramesAreRectified { get; set; }
    public string CameraRigCoordinateSystemId { get; set; } = "camera-rig";
    public string PlatformCoordinateSystemId { get; set; } = "platform";
    [Range(1, 10)] public int MaximumAttempts { get; set; } = 3;
    [Range(1, 60)] public int RetryDelaySeconds { get; set; } = 2;
    [Range(1, 300)] public int RequestTimeoutSeconds { get; set; } = 60;
    public string CalibrationUrl { get; set; } = "http://127.0.0.1:5208";
    public string CameraUrl { get; set; } = "http://127.0.0.1:5202";
    public string QualityUrl { get; set; } = "http://127.0.0.1:5203";
    public string DepthUrl { get; set; } = "http://127.0.0.1:5204";
    public string SegmentationUrl { get; set; } = "http://127.0.0.1:5205";
    public string AnalysisUrl { get; set; } = "http://127.0.0.1:5211";
    public string PositioningUrl { get; set; } = "http://127.0.0.1:5209";
    public string GeoreferenceUrl { get; set; } = "http://127.0.0.1:5210";
    public string StorageUrl { get; set; } = "http://127.0.0.1:5105";
}

public sealed record PipelineStageResult(string Name, string ArtifactUri, DateTimeOffset CompletedAt, int Attempts);
public sealed record PipelineStageFailure(string Stage, int Attempt, string Code, string Message, DateTimeOffset OccurredAt);
public sealed record PipelineProgress(Guid RunId, string Mode, IReadOnlyList<PipelineStageResult> Stages,
    string? ActiveStage = null, string? ErrorCode = null, string? ResultUri = null,
    string? CalibrationId = null, string? ModelVersion = null, string? TransformVersion = null,
    int ActiveAttempts = 0, IReadOnlyList<PipelineStageFailure>? Failures = null);

public sealed class PipelineException(string code, string message, bool transient = false, bool rejected = false)
    : Exception(message)
{
    public string Code { get; } = code;
    public bool Transient { get; } = transient;
    public bool Rejected { get; } = rejected;
}

public static class PipelineJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web) { IncludeFields = true };
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}
