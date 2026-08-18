using SmartMetrix.Domain;
using SmartMetrix.MeasurementOrchestrator;

namespace SmartMetrix.ArchitectureTests;

public sealed class MeasurementStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    [Fact]
    public void HappyPathCoversEveryProcessingTransition()
    {
        var process = NewProcess();
        var path = new[]
        {
            MeasurementStatus.Capturing,
            MeasurementStatus.QualityControl,
            MeasurementStatus.Reconstructing,
            MeasurementStatus.Analysing,
            MeasurementStatus.Georeferencing,
            MeasurementStatus.Completed
        };

        foreach (var target in path)
            process = MeasurementStateMachine.Transition(process, target, Guid.NewGuid(), null, Now, Timeout);

        Assert.Equal(MeasurementStatus.Completed, process.Status);
        Assert.Equal(path.Length, process.Version);
        Assert.Equal(path.Length, process.Transitions.Count);
        Assert.Null(process.StageDeadline);
    }

    [Fact]
    public void InvalidTransitionIsRejected()
    {
        Assert.Throws<InvalidMeasurementTransitionException>(() =>
            MeasurementStateMachine.Transition(NewProcess(), MeasurementStatus.Completed, Guid.NewGuid(), null, Now, Timeout));
    }

    [Fact]
    public void DuplicateCommandIsIdempotent()
    {
        var commandId = Guid.NewGuid();
        var first = MeasurementStateMachine.Transition(NewProcess(), MeasurementStatus.Capturing, commandId, "start", Now, Timeout);
        var duplicate = MeasurementStateMachine.Transition(first, MeasurementStatus.QualityControl, commandId, "duplicate", Now, Timeout);

        Assert.Same(first, duplicate);
        Assert.Equal(1, duplicate.Version);
    }

    [Theory]
    [InlineData(MeasurementStatus.Requested)]
    [InlineData(MeasurementStatus.Capturing)]
    [InlineData(MeasurementStatus.QualityControl)]
    [InlineData(MeasurementStatus.Reconstructing)]
    [InlineData(MeasurementStatus.Analysing)]
    [InlineData(MeasurementStatus.Georeferencing)]
    public void ActiveMeasurementCanBeCancelledOrFailed(MeasurementStatus status)
    {
        var process = At(status);
        var cancelled = MeasurementStateMachine.Transition(process, MeasurementStatus.Rejected, Guid.NewGuid(), "operator", Now, Timeout);
        var failed = MeasurementStateMachine.Transition(process, MeasurementStatus.Failed, Guid.NewGuid(), "timeout", Now, Timeout);

        Assert.Equal("operator", cancelled.FailureReason);
        Assert.Equal("timeout", failed.FailureReason);
    }

    [Theory]
    [InlineData(MeasurementStatus.Rejected)]
    [InlineData(MeasurementStatus.Failed)]
    public void RejectedOrFailedMeasurementCanBeRetried(MeasurementStatus status)
    {
        var terminal = MeasurementStateMachine.Transition(NewProcess(), status, Guid.NewGuid(), "reason", Now, Timeout);
        var retried = MeasurementStateMachine.Transition(terminal, MeasurementStatus.Requested, Guid.NewGuid(), "retry", Now, Timeout);

        Assert.Equal(MeasurementStatus.Requested, retried.Status);
        Assert.Null(retried.FailureReason);
        Assert.Equal(Now + Timeout, retried.StageDeadline);
    }

    [Fact]
    public void CompletedMeasurementIsTerminal()
    {
        var completed = At(MeasurementStatus.Completed);
        Assert.Throws<InvalidMeasurementTransitionException>(() =>
            MeasurementStateMachine.Transition(completed, MeasurementStatus.Requested, Guid.NewGuid(), "retry", Now, Timeout));
    }

    private static MeasurementProcess NewProcess() => new(Guid.NewGuid(), "EX-1", "QUARRY-LOCAL",
        MeasurementStatus.Requested, 0, Now, Now, Now + Timeout, null, [], new HashSet<Guid>());

    private static MeasurementProcess At(MeasurementStatus target)
    {
        var process = NewProcess();
        foreach (var next in new[] { MeasurementStatus.Capturing, MeasurementStatus.QualityControl,
                     MeasurementStatus.Reconstructing, MeasurementStatus.Analysing,
                     MeasurementStatus.Georeferencing, MeasurementStatus.Completed })
        {
            if (process.Status == target) break;
            process = MeasurementStateMachine.Transition(process, next, Guid.NewGuid(), null, Now, Timeout);
        }
        return process;
    }
}
