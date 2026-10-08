using System.Text.Json.Serialization;

namespace SmartMetrix.Domain.Excavation;

[JsonConverter(typeof(JsonStringEnumConverter<SignalQuality>))]
public enum SignalQuality { Good, Degraded, Unavailable }

/// <summary>One normalized, synchronized observation from a primary machine source.</summary>
public sealed record ExcavatorTelemetry(int SchemaVersion, Guid EventId, string ExcavatorId,
    string SourceId, string ClockId, long HardwareTimestampNanoseconds, DateTimeOffset RecordedAt,
    bool IsSynthetic, SignalQuality Quality, bool? EngineRunning, bool? ToolEngaged,
    bool? DischargeActive, bool? LoadPresent, double? SwingDegreesPerSecond,
    double? TravelMetresPerSecond);

[JsonConverter(typeof(JsonStringEnumConverter<ExcavationPhase>))]
public enum ExcavationPhase { Unknown, Waiting, Digging, LoadedSwing, Unloading, Returning }

[JsonConverter(typeof(JsonStringEnumConverter<CycleStatus>))]
public enum CycleStatus { Completed, Interrupted, Incomplete }

public sealed record PhaseSpan(DateTimeOffset Start, DateTimeOffset End, ExcavationPhase Phase,
    double Confidence, string Reason, Guid? SourceEventId)
{
    public double Seconds => (End - Start).TotalSeconds;
}

public sealed record ExcavationCycle(Guid CycleId, DateTimeOffset Start, DateTimeOffset End,
    CycleStatus Status, string Reason, IReadOnlyList<PhaseSpan> Phases);

public sealed record TelemetryReplay(string ExcavatorId, string SourceId, string ClockId, bool IsSynthetic,
    int InputCount, int DuplicateCount, DateTimeOffset FirstObservation, DateTimeOffset LastObservation,
    IReadOnlyList<PhaseSpan> Timeline, IReadOnlyList<ExcavationCycle> Cycles);

public sealed record CycleRecognitionOptions
{
    public const string AlgorithmVersion = "signal-rules-v1";
    public double MaximumGapSeconds { get; init; } = 3;
    public double MaximumClockSkewSeconds { get; init; } = .05;
    public double SwingThresholdDegreesPerSecond { get; init; } = 1;
    public double TravelThresholdMetresPerSecond { get; init; } = .05;

    public void Validate()
    {
        if (!double.IsFinite(MaximumGapSeconds) || MaximumGapSeconds <= 0 ||
            !double.IsFinite(MaximumClockSkewSeconds) || MaximumClockSkewSeconds < 0 ||
            !double.IsFinite(SwingThresholdDegreesPerSecond) || SwingThresholdDegreesPerSecond <= 0 ||
            !double.IsFinite(TravelThresholdMetresPerSecond) || TravelThresholdMetresPerSecond <= 0)
            throw new ArgumentException("Recognition thresholds must be finite and within their documented ranges.");
    }
}
