namespace SmartMetrix.E2ESimulator;

public sealed record GoldenDataset(
    string DatasetVersion,
    string ModelVersion,
    string CalibrationVersion,
    string TransformVersion,
    int Width,
    int Height,
    long HardwareTimestampNanoseconds,
    double DistanceMetres,
    double ExpectedD50Millimetres,
    double ExpectedD80Millimetres,
    GoldenPosition ExpectedPosition,
    AcceptanceCriteria Acceptance);

public sealed record GoldenPosition(double XMetres, double YMetres, double ZMetres);

public sealed record AcceptanceCriteria(
    double MaximumD50RelativeError,
    double MaximumD80RelativeError,
    double MaximumCoordinateErrorMetres,
    double MaximumDurationMilliseconds,
    double MaximumRejectedFrameFraction);

public sealed record StageDiagnostic(
    string Stage,
    string Version,
    double DurationMilliseconds,
    IReadOnlyDictionary<string, double> Metrics,
    IReadOnlyList<string> Artifacts);

public sealed record E2EReport(
    Guid MeasurementId,
    string DatasetVersion,
    string ModelVersion,
    string CalibrationVersion,
    string TransformVersion,
    DateTimeOffset CompletedAt,
    double D50Millimetres,
    double D80Millimetres,
    double D50RelativeError,
    double D80RelativeError,
    double CoordinateErrorMetres,
    double RejectedFrameFraction,
    double DurationMilliseconds,
    IReadOnlyList<StageDiagnostic> Stages,
    IReadOnlyDictionary<string, bool> Checks,
    bool Passed);
