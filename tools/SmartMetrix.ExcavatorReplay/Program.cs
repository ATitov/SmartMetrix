using System.Globalization;
using SmartMetrix.Domain.Excavation;
using SmartMetrix.ExcavatorReplay;

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("ExcavatorReplay --input file.jsonl --output directory --start ISO8601 --end ISO8601 [--max-gap seconds]");
    Console.WriteLine("No arguments: bundled synthetic scenario, 04.10.2026 08:00–08:02 UTC+06:00. No hardware connection.");
    return 0;
}
try
{
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var allowed = new[] { "--input", "--output", "--start", "--end", "--max-gap" };
    for (var i = 0; i < args.Length; i += 2)
    {
        if (!allowed.Contains(args[i], StringComparer.Ordinal) || i + 1 == args.Length || !values.TryAdd(args[i], args[i + 1]))
            throw new ArgumentException("Unknown, repeated or incomplete command option.");
    }
    if (values.ContainsKey("--input") && (!values.ContainsKey("--start") || !values.ContainsKey("--end")))
        throw new ArgumentException("Custom input requires explicit --start and --end shift bounds.");
    var input = values.GetValueOrDefault("--input", Path.Combine(AppContext.BaseDirectory, "dataset", "synthetic-v1.jsonl"));
    var output = values.GetValueOrDefault("--output", Path.Combine("artifacts", "excavator-replay"));
    static DateTimeOffset Bound(string value)
    {
        if (!(value.EndsWith('Z') || value.Length >= 6 && value[^3] == ':' && value[^6] is '+' or '-'))
            throw new ArgumentException("Shift bounds must include an explicit UTC offset or Z.");
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);
    }
    var start = Bound(values.GetValueOrDefault("--start", "2026-10-04T08:00:00+06:00"));
    var end = Bound(values.GetValueOrDefault("--end", "2026-10-04T08:02:00+06:00"));
    var options = new CycleRecognitionOptions
    {
        MaximumGapSeconds = double.Parse(values.GetValueOrDefault("--max-gap", "3"), CultureInfo.InvariantCulture)
    };
    var result = ReplayRunner.Run(input, output, start, end, options);
    Console.WriteLine($"{(result.Report.IsSynthetic ? "SYNTHETIC" : "RECORDED")} — {CycleRecognitionOptions.AlgorithmVersion}");
    Console.WriteLine($"Completed cycles: {result.Report.CompletedCycles}; interrupted: {result.Report.InterruptedCycles}; incomplete: {result.Report.IncompleteCycles}");
    Console.WriteLine($"Coverage: {result.Report.CoverageFraction:P1}; artifacts {(result.Reused ? "verified and reused" : "saved")}: {result.Directory}");
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or IOException or FormatException or OverflowException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Replay failed: " + exception.Message);
    return 1;
}
