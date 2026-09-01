using SmartMetrix.Domain;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed record MeasurementTransition(
    long Version,
    MeasurementStatus From,
    MeasurementStatus To,
    DateTimeOffset OccurredAt,
    Guid CommandId,
    string Reason);

public sealed record MeasurementProcess(
    Guid Id,
    string ExcavatorId,
    string CoordinateSystemId,
    MeasurementStatus Status,
    long Version,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StageDeadline,
    string? FailureReason,
    IReadOnlyList<MeasurementTransition> Transitions,
    HashSet<Guid> ProcessedCommands,
    double? D10 = null,
    double? D20 = null,
    double? D50 = null,
    double? D80 = null,
    double? D90 = null,
    double? Confidence = null,
    int? BlockCount = null,
    double? OversizeFraction = null,
    double? Coverage = null,
    string? AlgorithmVersion = null,
    bool IsTestData = false);

public sealed class InvalidMeasurementTransitionException(MeasurementStatus from, MeasurementStatus to)
    : InvalidOperationException($"Transition from {from} to {to} is not allowed.");

public sealed class MeasurementConcurrencyException(Guid id, long expected, long actual)
    : InvalidOperationException($"Measurement {id} version is {actual}, expected {expected}.");

public static class MeasurementStateMachine
{
    private static readonly Dictionary<MeasurementStatus, MeasurementStatus[]> Allowed =
        new Dictionary<MeasurementStatus, MeasurementStatus[]>
        {
            [MeasurementStatus.Requested] = [MeasurementStatus.Capturing, MeasurementStatus.Rejected, MeasurementStatus.Failed],
            [MeasurementStatus.Capturing] = [MeasurementStatus.QualityControl, MeasurementStatus.Rejected, MeasurementStatus.Failed],
            [MeasurementStatus.QualityControl] = [MeasurementStatus.Reconstructing, MeasurementStatus.Rejected, MeasurementStatus.Failed],
            [MeasurementStatus.Reconstructing] = [MeasurementStatus.Analysing, MeasurementStatus.Rejected, MeasurementStatus.Failed],
            [MeasurementStatus.Analysing] = [MeasurementStatus.Georeferencing, MeasurementStatus.Rejected, MeasurementStatus.Failed],
            [MeasurementStatus.Georeferencing] = [MeasurementStatus.Completed, MeasurementStatus.Rejected, MeasurementStatus.Failed],
            [MeasurementStatus.Rejected] = [MeasurementStatus.Requested],
            [MeasurementStatus.Failed] = [MeasurementStatus.Requested],
            [MeasurementStatus.Completed] = []
        };

    public static MeasurementProcess Transition(MeasurementProcess current, MeasurementStatus target, Guid commandId,
        string? reason, DateTimeOffset now, TimeSpan stageTimeout)
    {
        if (current.ProcessedCommands.Contains(commandId)) return current;
        if (!Allowed[current.Status].Contains(target)) throw new InvalidMeasurementTransitionException(current.Status, target);

        var version = current.Version + 1;
        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? $"{current.Status} -> {target}" : reason.Trim();
        var terminal = target is MeasurementStatus.Completed or MeasurementStatus.Rejected or MeasurementStatus.Failed;
        return current with
        {
            Status = target,
            Version = version,
            UpdatedAt = now,
            StageDeadline = terminal ? null : now + stageTimeout,
            FailureReason = target is MeasurementStatus.Rejected or MeasurementStatus.Failed ? effectiveReason : null,
            Transitions = current.Transitions.Append(new MeasurementTransition(version, current.Status, target, now, commandId, effectiveReason)).ToArray(),
            ProcessedCommands = new HashSet<Guid>(current.ProcessedCommands) { commandId }
        };
    }
}
