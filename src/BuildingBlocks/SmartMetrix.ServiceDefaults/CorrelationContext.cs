using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SmartMetrix.ServiceDefaults;

public static partial class CorrelationContext
{
    public const string CorrelationHeader = "X-Correlation-ID";
    public const string MeasurementHeader = "X-Measurement-ID";

    public static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        ContextValueRegex().IsMatch(value);

    public static string CreateCorrelationId() =>
        Activity.Current?.TraceId.ToString() is { Length: > 0 } traceId
            ? traceId
            : Guid.CreateVersion7().ToString("N");

    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ContextValueRegex();
}
