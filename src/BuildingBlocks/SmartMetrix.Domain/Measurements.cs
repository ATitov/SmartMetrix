using System.Text.Json.Serialization;

namespace SmartMetrix.Domain;

public readonly record struct MeasurementId(Guid Value)
{
    public static MeasurementId New() => new(Guid.CreateVersion7());
    public override string ToString() => Value.ToString();
}

[JsonConverter(typeof(JsonStringEnumConverter<MeasurementStatus>))]
public enum MeasurementStatus
{
    Requested,
    Capturing,
    QualityControl,
    Reconstructing,
    Analysing,
    Georeferencing,
    Completed,
    Rejected,
    Failed,
    Segmenting,
    Persisting
}

public sealed record Measurement(
    MeasurementId Id,
    string ExcavatorId,
    DateTimeOffset RequestedAt,
    MeasurementStatus Status,
    string CoordinateSystemId,
    string CalibrationId);
