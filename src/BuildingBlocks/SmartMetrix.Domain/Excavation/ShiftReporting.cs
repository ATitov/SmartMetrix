using System.Globalization;
using System.Text;

namespace SmartMetrix.Domain.Excavation;

public sealed record ShiftReport(string AlgorithmVersion, string ExcavatorId, string SourceId, string ClockId,
    bool IsSynthetic, DateTimeOffset Start, DateTimeOffset End, double CoverageFraction,
    int StartedCycles, int CompletedCycles, int InterruptedCycles, int IncompleteCycles,
    IReadOnlyDictionary<ExcavationPhase, double> PhaseSeconds, IReadOnlyList<PhaseSpan> Timeline,
    IReadOnlyList<ExcavationCycle> Cycles, double? MassTonnes, double? TonnesPerHour, double? LitresPerTonne);

public static class ShiftReporting
{
    public static ShiftReport Build(TelemetryReplay replay, DateTimeOffset start, DateTimeOffset end)
    {
        ArgumentNullException.ThrowIfNull(replay);
        if (end <= start || start.Offset != end.Offset) throw new ArgumentException("Shift requires increasing bounds with the same fixed UTC offset.");
        var spans = new List<PhaseSpan>();
        var cursor = start;
        foreach (var span in replay.Timeline.Where(x => x.End > start && x.Start < end))
        {
            var left = span.Start < start ? start : span.Start;
            var right = span.End > end ? end : span.End;
            if (left > cursor) spans.Add(new(cursor, left, ExcavationPhase.Unknown, 0, "outside-recording", null));
            spans.Add(span with { Start = left, End = right });
            cursor = right;
        }
        if (cursor < end) spans.Add(new(cursor, end, ExcavationPhase.Unknown, 0, "outside-recording", null));
        var totals = Enum.GetValues<ExcavationPhase>().ToDictionary(x => x, x => spans.Where(s => s.Phase == x).Sum(s => s.Seconds));
        var cycles = replay.Cycles.Where(x => x.End >= start && x.Start < end).ToArray();
        // Completion belongs to the shift containing its end; [start, end) avoids counting twice.
        int Ended(CycleStatus status) => replay.Cycles.Count(x => x.Status == status && x.End >= start && x.End < end);
        return new(CycleRecognitionOptions.AlgorithmVersion, replay.ExcavatorId, replay.SourceId, replay.ClockId,
            replay.IsSynthetic, start, end, 1 - totals[ExcavationPhase.Unknown] / (end - start).TotalSeconds,
            replay.Cycles.Count(x => x.Start >= start && x.Start < end), Ended(CycleStatus.Completed),
            Ended(CycleStatus.Interrupted), cycles.Count(x => x.Status == CycleStatus.Incomplete), totals, spans, cycles, null, null, null);
    }

    public static string ToCsv(ShiftReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder("excavatorId,start,end,isSynthetic,coverageFraction,startedCycles,completedCycles,interruptedCycles,incompleteCycles,massTonnes,tonnesPerHour,litresPerTonne");
        foreach (var phase in Enum.GetValues<ExcavationPhase>()) text.Append(',').Append(phase).Append("Seconds");
        text.AppendLine();
        text.Append(Escape(report.ExcavatorId)).Append(',').Append(report.Start.ToString("O", CultureInfo.InvariantCulture))
            .Append(',').Append(report.End.ToString("O", CultureInfo.InvariantCulture)).Append(',').Append(report.IsSynthetic ? "true" : "false")
            .Append(',').Append(report.CoverageFraction.ToString("R", CultureInfo.InvariantCulture))
            .Append(',').Append(report.StartedCycles).Append(',').Append(report.CompletedCycles).Append(',').Append(report.InterruptedCycles)
            .Append(',').Append(report.IncompleteCycles).Append(",,,");
        foreach (var phase in Enum.GetValues<ExcavationPhase>()) text.Append(',').Append(report.PhaseSeconds[phase].ToString("R", CultureInfo.InvariantCulture));
        return text.AppendLine().ToString();
    }

    private static string Escape(string value)
    {
        // Spreadsheet formula injection is prevented even for identifiers beginning with an operator.
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0], StringComparison.Ordinal)) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
