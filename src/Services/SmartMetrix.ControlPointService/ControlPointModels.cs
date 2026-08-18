namespace SmartMetrix.ControlPointService;

public enum CoordinateUnit { Metres, Millimetres }
public enum ControlPointStatus { Active, Inactive }
public enum ControlTargetType { CodedVisualMark, Prism }

public sealed record CoordinateInput(double X, double Y, double Z, CoordinateUnit Unit = CoordinateUnit.Metres);
public sealed record ControlTarget(ControlTargetType Type, string Identifier, double? PrismConstantMillimetres = null);
public sealed record CreateControlPointRequest(string PointId, CoordinateInput Coordinates, string CoordinateSystemId,
    double AccuracyMillimetres, DateTimeOffset MeasuredAt, ControlPointStatus Status, ControlTarget Target);
public sealed record UpdateControlPointRequest(CoordinateInput Coordinates, string CoordinateSystemId,
    double AccuracyMillimetres, DateTimeOffset MeasuredAt, ControlPointStatus Status, ControlTarget Target, string? Reason = null);
public sealed record AddSurveyObservationRequest(CoordinateInput Coordinates, double AccuracyMillimetres,
    DateTimeOffset MeasuredAt, string InstrumentId);
public sealed record ControlPointVersion(int Version, double XMetres, double YMetres, double ZMetres,
    string CoordinateSystemId, double AccuracyMillimetres, DateTimeOffset MeasuredAt, ControlPointStatus Status,
    ControlTarget Target, DateTimeOffset ChangedAt, string ChangedBy, string? Reason);
public sealed record ControlPointView(string PointId, int Version, double XMetres, double YMetres, double ZMetres,
    string CoordinateSystemId, double AccuracyMillimetres, DateTimeOffset MeasuredAt, ControlPointStatus Status,
    ControlTarget Target);
public sealed record SurveyObservation(Guid Id, string PointId, double XMetres, double YMetres, double ZMetres,
    double AccuracyMillimetres, DateTimeOffset MeasuredAt, string InstrumentId, DateTimeOffset RecordedAt);
public sealed record ControlPointAuditEntry(string PointId, string Action, DateTimeOffset At, string Actor, string? Details);
public sealed record ControlPointUsage(string PointId, Guid MeasurementId, DateTimeOffset RecordedAt);
public sealed record ImportError(int Item, string? PointId, string Field, string Message);
public sealed record ImportReport(int Total, int Imported, IReadOnlyList<ImportError> Errors);

public sealed class ControlPointValidationException(string message) : Exception(message);
public sealed class ControlPointConflictException(string message) : Exception(message);
