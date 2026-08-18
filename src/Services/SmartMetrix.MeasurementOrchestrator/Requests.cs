using SmartMetrix.Domain;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed record StartMeasurementRequest(Guid? CommandId, Guid? MeasurementId, string ExcavatorId, string CoordinateSystemId, string? Reason);
public sealed record WorkflowCommand(Guid CommandId, long ExpectedVersion, string? Reason);
public sealed record TransitionRequest(Guid CommandId, long ExpectedVersion, MeasurementStatus Target, string? Reason);
