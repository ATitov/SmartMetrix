namespace SmartMetrix.Domain.Excavation;

public static class CycleRecognition
{
    public static TelemetryReplay Replay(IReadOnlyList<ExcavatorTelemetry> input, CycleRecognitionOptions? options = null)
    {
        options ??= new();
        options.Validate();
        ArgumentNullException.ThrowIfNull(input);
        if (input.Count < 2) throw new ArgumentException("Replay requires at least two observations.", nameof(input));
        var seen = new Dictionary<Guid, ExcavatorTelemetry>();
        var samples = new List<ExcavatorTelemetry>();
        foreach (var sample in input)
        {
            Validate(sample);
            if (seen.TryGetValue(sample.EventId, out var prior))
            {
                if (prior != sample) throw new ArgumentException("Conflicting duplicate EventId.", nameof(input));
                continue;
            }
            seen.Add(sample.EventId, sample);
            samples.Add(sample);
        }
        if (samples.Count < 2) throw new ArgumentException("Replay requires at least two distinct observations.", nameof(input));
        var first = samples[0];
        var spans = new List<PhaseSpan>();
        for (var i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (sample.ExcavatorId != first.ExcavatorId || sample.SourceId != first.SourceId ||
                sample.ClockId != first.ClockId || sample.IsSynthetic != first.IsSynthetic)
                throw new ArgumentException("A replay must use one machine, primary source, clock and data mode.", nameof(input));
            if (i == 0) continue;
            var before = samples[i - 1];
            if (sample.RecordedAt <= before.RecordedAt || sample.HardwareTimestampNanoseconds <= before.HardwareTimestampNanoseconds)
                throw new ArgumentException("Distinct observations must have strictly increasing timestamps.", nameof(input));
            var seconds = (sample.RecordedAt - before.RecordedAt).TotalSeconds;
            var hardwareSeconds = (sample.HardwareTimestampNanoseconds - before.HardwareTimestampNanoseconds) / 1e9;
            if (Math.Abs(seconds - hardwareSeconds) > options.MaximumClockSkewSeconds)
                throw new ArgumentException("Hardware and mapped wall clocks disagree.", nameof(input));
            var phase = seconds > options.MaximumGapSeconds
                ? (ExcavationPhase.Unknown, 0d, "telemetry-gap")
                : Classify(before, options);
            var span = new PhaseSpan(before.RecordedAt, sample.RecordedAt, phase.Item1, phase.Item2, phase.Item3, before.EventId);
            // Retain source observations, even within a continuous phase, for reproducible provenance.
            spans.Add(span);
        }
        return new(first.ExcavatorId, first.SourceId, first.ClockId, first.IsSynthetic, input.Count,
            input.Count - samples.Count, first.RecordedAt, samples[^1].RecordedAt, spans, Recognize(spans, Classify(samples[^1], options).Item1));
    }

    private static (ExcavationPhase, double, string) Classify(ExcavatorTelemetry sample, CycleRecognitionOptions options)
    {
        if (sample.Quality != SignalQuality.Good) return (ExcavationPhase.Unknown, 0, "signal-quality");
        if (sample.EngineRunning is null) return (ExcavationPhase.Unknown, 0, "missing-engine-state");
        if (sample.EngineRunning == false) return (ExcavationPhase.Waiting, 1, "engine-off");
        if (sample.TravelMetresPerSecond is null) return (ExcavationPhase.Unknown, 0, "missing-travel-signal");
        if (Math.Abs(sample.TravelMetresPerSecond.Value) > options.TravelThresholdMetresPerSecond)
            return (ExcavationPhase.Unknown, 0, "machine-travelling");
        if (sample.ToolEngaged is null || sample.DischargeActive is null || sample.LoadPresent is null || sample.SwingDegreesPerSecond is null)
            return (ExcavationPhase.Unknown, 0, "missing-work-signals");
        var swinging = Math.Abs(sample.SwingDegreesPerSecond.Value) > options.SwingThresholdDegreesPerSecond;
        if (sample.ToolEngaged == true && (sample.DischargeActive == true || swinging))
            return (ExcavationPhase.Unknown, 0, "conflicting-work-signals");
        if (sample.DischargeActive == true) return (ExcavationPhase.Unloading, 1, "discharge-active");
        if (sample.ToolEngaged == true) return (ExcavationPhase.Digging, 1, "tool-engaged");
        if (swinging) return sample.LoadPresent == true
            ? (ExcavationPhase.LoadedSwing, 1, "swing-with-load")
            : (ExcavationPhase.Returning, 1, "swing-without-load");
        if (sample.LoadPresent == true) return (ExcavationPhase.Unknown, 0, "stationary-loaded");
        return (ExcavationPhase.Waiting, 1, "idle-reason-unknown");
    }

    private static List<ExcavationCycle> Recognize(List<PhaseSpan> timeline, ExcavationPhase finalPhase)
    {
        var cycles = new List<ExcavationCycle>();
        var active = new List<PhaseSpan>();
        var expected = new[] { ExcavationPhase.Digging, ExcavationPhase.LoadedSwing, ExcavationPhase.Unloading, ExcavationPhase.Returning };
        var stage = 0;
        void Finish(CycleStatus status, string reason)
        {
            if (active.Count == 0) return;
            cycles.Add(new(active[0].SourceEventId!.Value, active[0].Start, active[^1].End, status, reason, active.ToArray()));
            active.Clear();
            stage = 0;
        }
        foreach (var span in timeline)
        {
            if (active.Count > 0)
            {
                if (stage == 3 && span.Phase is ExcavationPhase.Digging or ExcavationPhase.Waiting)
                    Finish(CycleStatus.Completed, "ordered-phases");
                else if (span.Phase == expected[stage]) { active.Add(span); continue; }
                else if (stage < 3 && span.Phase == expected[stage + 1]) { stage++; active.Add(span); continue; }
                else Finish(CycleStatus.Interrupted, span.Phase == ExcavationPhase.Unknown ? span.Reason : "unexpected-phase");
            }
            if (span.Phase == ExcavationPhase.Digging) active.Add(span);
        }
        if (active.Count > 0 && stage == 3 && finalPhase is ExcavationPhase.Waiting or ExcavationPhase.Digging)
            Finish(CycleStatus.Completed, "ordered-phases");
        else Finish(CycleStatus.Incomplete, "end-of-recording");
        return cycles;
    }

    private static void Validate(ExcavatorTelemetry sample)
    {
        if (sample.SchemaVersion != 1 || sample.EventId == Guid.Empty ||
            string.IsNullOrWhiteSpace(sample.ExcavatorId) || string.IsNullOrWhiteSpace(sample.SourceId) ||
            string.IsNullOrWhiteSpace(sample.ClockId) || sample.HardwareTimestampNanoseconds < 0 ||
            sample.RecordedAt == default || !Enum.IsDefined(sample.Quality) ||
            sample.SwingDegreesPerSecond is { } swing && !double.IsFinite(swing) ||
            sample.TravelMetresPerSecond is { } travel && !double.IsFinite(travel))
            throw new ArgumentException("Invalid telemetry schema, identity, timestamp, quality or signal value.", nameof(sample));
    }
}
