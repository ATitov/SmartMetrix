namespace SmartMetrix.Contracts;

public sealed record MeasurementStateChanged(Guid MeasurementId, long Version, string Status);
