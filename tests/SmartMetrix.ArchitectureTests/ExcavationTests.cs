using System.Text.Json;
using SmartMetrix.Domain.Excavation;
using SmartMetrix.ExcavatorReplay;

namespace SmartMetrix.ArchitectureTests;

public sealed class ExcavationTests
{
    private static readonly DateTimeOffset Origin = new(2026, 10, 4, 8, 0, 0, TimeSpan.FromHours(6));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static ExcavatorTelemetry Sample(int second, ExcavationPhase phase) => new(1,
        new Guid(second + 1, 0, 0, new byte[8]), "excavator-1", "fixture", "clock-1", second * 1_000_000_000L,
        Origin.AddSeconds(second), true, SignalQuality.Good, true, phase == ExcavationPhase.Digging,
        phase == ExcavationPhase.Unloading, phase == ExcavationPhase.LoadedSwing,
        phase is ExcavationPhase.LoadedSwing or ExcavationPhase.Returning ? 10 : 0, 0);

    private static ExcavatorTelemetry[] CompleteCycle() =>
    [Sample(0, ExcavationPhase.Digging), Sample(1, ExcavationPhase.LoadedSwing),
     Sample(2, ExcavationPhase.Unloading), Sample(3, ExcavationPhase.Returning),
     Sample(4, ExcavationPhase.Waiting), Sample(5, ExcavationPhase.Waiting)];

    [Fact]
    public void CompleteCycleHasStableIdentityAndDuplicateDoesNotChangeTotals()
    {
        var samples = CompleteCycle();
        var baseline = CycleRecognition.Replay(samples);
        var replay = CycleRecognition.Replay(samples.Concat([samples[1]]).ToArray());
        var cycle = Assert.Single(replay.Cycles);
        Assert.Equal(CycleStatus.Completed, cycle.Status);
        Assert.Equal(samples[0].EventId, cycle.CycleId);
        Assert.Equal(baseline.Timeline, replay.Timeline);
        Assert.Equal(1, replay.DuplicateCount);
        Assert.Equal(4, (cycle.End - cycle.Start).TotalSeconds);
    }

    [Fact]
    public void ConflictingDuplicateIsRejected()
    {
        var samples = CompleteCycle();
        Assert.Throws<ArgumentException>(() => CycleRecognition.Replay(samples.Concat([samples[0] with { LoadPresent = true }]).ToArray()));
    }

    [Fact]
    public void GapInterruptsCycleAndIsUnknownTime()
    {
        var samples = CompleteCycle();
        var replay = CycleRecognition.Replay([samples[0], samples[1], Sample(10, ExcavationPhase.Unloading), Sample(11, ExcavationPhase.Returning), Sample(12, ExcavationPhase.Waiting)]);
        Assert.Equal(CycleStatus.Interrupted, Assert.Single(replay.Cycles).Status);
        var gap = Assert.Single(replay.Timeline, x => x.Reason == "telemetry-gap");
        Assert.Equal(9, gap.Seconds);
        Assert.Equal(ExcavationPhase.Unknown, gap.Phase);
        Assert.DoesNotContain(replay.Cycles, x => x.Status == CycleStatus.Completed);
    }

    [Theory]
    [InlineData("clock")]
    [InlineData("order")]
    [InlineData("source")]
    [InlineData("hardware")]
    [InlineData("synthetic")]
    public void IncompatibleOrOutOfOrderObservationsAreRejected(string kind)
    {
        var first = Sample(0, ExcavationPhase.Waiting);
        var second = Sample(1, ExcavationPhase.Waiting);
        second = kind switch
        {
            "clock" => second with { ClockId = "other" },
            "source" => second with { SourceId = "other" },
            "order" => second with { RecordedAt = Origin },
            "hardware" => second with { HardwareTimestampNanoseconds = 2_000_000_000L },
            "synthetic" => second with { IsSynthetic = false },
            _ => throw new ArgumentException(kind)
        };
        Assert.Throws<ArgumentException>(() => CycleRecognition.Replay([first, second]));
    }

    [Fact]
    public void MissingWorkSignalIsUnknownNotWaiting()
    {
        var first = Sample(0, ExcavationPhase.Waiting) with { LoadPresent = null };
        var replay = CycleRecognition.Replay([first, Sample(1, ExcavationPhase.Waiting)]);
        Assert.Equal(ExcavationPhase.Unknown, Assert.Single(replay.Timeline).Phase);
        Assert.Equal("missing-work-signals", replay.Timeline[0].Reason);
    }

    [Fact]
    public void IncompleteAndSkippedPhasesNeverCountAsComplete()
    {
        var incomplete = CycleRecognition.Replay(CompleteCycle()[..4]);
        Assert.Equal(CycleStatus.Incomplete, Assert.Single(incomplete.Cycles).Status);
        var skipped = CycleRecognition.Replay([Sample(0, ExcavationPhase.Digging), Sample(1, ExcavationPhase.Unloading), Sample(2, ExcavationPhase.Waiting)]);
        Assert.Equal(CycleStatus.Interrupted, Assert.Single(skipped.Cycles).Status);
    }

    [Fact]
    public void AdjacentShiftsCountBoundaryCompletionOnceAndClipPhaseTime()
    {
        var replay = CycleRecognition.Replay(CompleteCycle());
        var before = ShiftReporting.Build(replay, Origin, Origin.AddSeconds(4));
        var after = ShiftReporting.Build(replay, Origin.AddSeconds(4), Origin.AddSeconds(6));
        Assert.Equal(0, before.CompletedCycles);
        Assert.Equal(1, after.CompletedCycles);
        Assert.Equal(replay.Cycles[0].CycleId, Assert.Single(after.Cycles).CycleId);
        Assert.Equal(4, before.PhaseSeconds.Values.Sum());
        Assert.Equal(2, after.PhaseSeconds.Values.Sum());
        Assert.Equal(.5, after.CoverageFraction);
        Assert.Null(after.MassTonnes);
        Assert.Null(after.TonnesPerHour);
        Assert.Null(after.LitresPerTonne);
    }

    [Fact]
    public void ShiftWithoutObservationsIsAllUnknown()
    {
        var replay = CycleRecognition.Replay(CompleteCycle());
        var report = ShiftReporting.Build(replay, Origin.AddHours(1), Origin.AddHours(2));
        Assert.Equal(0, report.CoverageFraction);
        Assert.Equal(3600, report.PhaseSeconds[ExcavationPhase.Unknown]);
        Assert.Empty(report.Cycles);
    }

    [Fact]
    public void CsvHasBlankMassColumnsAndNeutralizesFormulaIdentifiers()
    {
        var replay = CycleRecognition.Replay(CompleteCycle().Select(x => x with { ExcavatorId = "=1+1" }).ToArray());
        var csv = ShiftReporting.ToCsv(ShiftReporting.Build(replay, Origin, Origin.AddSeconds(5)));
        Assert.Contains("\"'=1+1\"", csv, StringComparison.Ordinal);
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var values = lines[1].Split(',');
        Assert.Equal(lines[0].Split(',').Length, values.Length);
        Assert.All(values[9..12], value => Assert.Equal("", value));
    }

    [Fact]
    public void ReplayIsDurableIdempotentAndDetectsCorruptedArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "smartmetrix-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "input.jsonl");
            File.WriteAllLines(input, CompleteCycle().Select(x => JsonSerializer.Serialize(x, JsonOptions)));
            var first = ReplayRunner.Run(input, root, Origin, Origin.AddSeconds(5));
            var again = ReplayRunner.Run(input, root, Origin, Origin.AddSeconds(5));
            Assert.False(first.Reused);
            Assert.True(again.Reused);
            Assert.Equal(first.RunId, again.RunId);
            Assert.Equal(1, again.Report.CompletedCycles);
            File.WriteAllText(Path.Combine(first.Directory, "shift-report.csv"), "corrupted");
            Assert.Throws<IOException>(() => ReplayRunner.Run(input, root, Origin, Origin.AddSeconds(5)));
        }
        finally { Directory.Delete(root, true); }
    }
}
