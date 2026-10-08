using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SmartMetrix.Domain.Excavation;

namespace SmartMetrix.ApiGateway;

public sealed class ExcavationReportOptions
{
    public const string Section = "ExcavationReports";
    public Dictionary<string, string> Roots { get; set; } = new(StringComparer.Ordinal);
}

public sealed record ExcavationRunSummary(string RunId, string ExcavatorId, string SourceId, bool IsSynthetic,
    DateTimeOffset Start, DateTimeOffset End, double CoverageFraction, int CompletedCycles,
    int InterruptedCycles, int IncompleteCycles);
public sealed record ExcavationRunList(string State, IReadOnlyList<ExcavationRunSummary> Items, int InvalidCount, bool Truncated);
public sealed record ExcavationRunReport(string RunId, string InputSha256, ShiftReport Report);

public sealed class ExcavationReportStore(IOptions<ExcavationReportOptions> options, IWebHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true
    };
    private const int MaximumRuns = 1000;
    private const int MaximumReportBytes = 32 * 1024 * 1024;

    public async Task<ExcavationRunList> ListAsync(WorkstationScope area, CancellationToken ct)
    {
        var root = Root(area.Id);
        if (root is null) return new("NotConfigured", [], 0, false);
        if (!Directory.Exists(root)) return new("Unavailable", [], 0, false);
        var items = new List<ExcavationRunSummary>();
        var invalid = 0;
        string[] directories;
        try { directories = Directory.EnumerateDirectories(root).Where(x => Identifier(Path.GetFileName(x))).Take(MaximumRuns + 1).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return new("Unavailable", [], 0, false); }
        foreach (var directory in directories.Take(MaximumRuns))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var run = await ReadAsync(directory, Path.GetFileName(directory), ct);
                if (run.Report.ExcavatorId != area.ExcavatorId) continue;
                var report = run.Report;
                items.Add(new(run.RunId, report.ExcavatorId, report.SourceId, report.IsSynthetic, report.Start,
                    report.End, report.CoverageFraction, report.CompletedCycles, report.InterruptedCycles, report.IncompleteCycles));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException)
            { invalid++; }
        }
        return new(invalid == 0 ? "Ready" : "Degraded", items.OrderByDescending(x => x.Start).ThenBy(x => x.RunId, StringComparer.Ordinal).ToArray(),
            invalid, directories.Length > MaximumRuns);
    }

    public async Task<ExcavationRunReport> GetAsync(WorkstationScope area, string runId, CancellationToken ct)
    {
        if (!Identifier(runId)) throw new WorkstationApiException(400, "InvalidReplayId");
        var root = Root(area.Id) ?? throw new WorkstationApiException(409, "ReplayNotConfigured");
        var directory = Path.Combine(root, runId);
        if (!Directory.Exists(directory)) throw new WorkstationApiException(404, "ReplayNotFound");
        ExcavationRunReport run;
        try { run = await ReadAsync(directory, runId, ct); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new WorkstationApiException(409, "ReplayArtifactsInvalid"); }
        if (run.Report.ExcavatorId != area.ExcavatorId) throw new WorkstationApiException(404, "ReplayNotFound");
        return run;
    }

    private string? Root(string scopeId)
    {
        if (!options.Value.Roots.TryGetValue(scopeId, out var configured) || string.IsNullOrWhiteSpace(configured)) return null;
        return Path.GetFullPath(configured, environment.ContentRootPath);
    }

    private static bool Identifier(string value) => value.Length == 64 && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task<byte[]> BytesAsync(string path, int maximum, CancellationToken ct)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > maximum)
            throw new IOException("Invalid replay artifact.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > maximum) throw new IOException("Replay artifact exceeds limit.");
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static async Task<ExcavationRunReport> ReadAsync(string directory, string runId, CancellationToken ct)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked replay directory.");
        using var manifest = JsonDocument.Parse(await BytesAsync(Path.Combine(directory, "manifest.json"), 65536, ct));
        var metadata = manifest.RootElement;
        if (metadata.GetProperty("schemaVersion").GetInt32() != 1 || metadata.GetProperty("runId").GetString() != runId)
            throw new IOException("Replay identity mismatch.");
        var bytes = await BytesAsync(Path.Combine(directory, "shift-report.json"), MaximumReportBytes, ct);
        var checksum = metadata.GetProperty("artifactSha256").GetProperty("shift-report.json").GetString();
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), checksum, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Replay checksum mismatch.");
        var report = JsonSerializer.Deserialize<ShiftReport>(bytes, JsonOptions) ?? throw new JsonException("Missing report.");
        if (report.Start != metadata.GetProperty("shiftStart").GetDateTimeOffset() ||
            report.End != metadata.GetProperty("shiftEnd").GetDateTimeOffset() ||
            report.IsSynthetic != metadata.GetProperty("isSynthetic").GetBoolean() ||
            report.AlgorithmVersion != metadata.GetProperty("algorithmVersion").GetString() ||
            !double.IsFinite(report.CoverageFraction) || report.CoverageFraction is < 0 or > 1 || report.End <= report.Start ||
            string.IsNullOrWhiteSpace(report.ExcavatorId) || report.Timeline is null || report.Cycles is null || report.PhaseSeconds is null ||
            Enum.GetValues<ExcavationPhase>().Any(x => !report.PhaseSeconds.TryGetValue(x, out var seconds) || !double.IsFinite(seconds) || seconds < 0))
            throw new IOException("Invalid report metadata.");
        var inputHash = metadata.GetProperty("inputSha256").GetString();
        if (inputHash is null || !Identifier(inputHash)) throw new IOException("Invalid input provenance.");
        return new(runId, inputHash, report);
    }
}

public static class ExcavationReportEndpoints
{
    public static void MapExcavationReports(this RouteGroupBuilder scope)
    {
        scope.MapGet("/excavation/runs", (HttpContext context, ExcavationReportStore store, CancellationToken ct) =>
            store.ListAsync((WorkstationScope)context.Items["workstation.scope"]!, ct));
        scope.MapGet("/excavation/runs/{runId}/report", (string runId, HttpContext context, ExcavationReportStore store, CancellationToken ct) =>
            store.GetAsync((WorkstationScope)context.Items["workstation.scope"]!, runId, ct));
        scope.MapGet("/excavation/runs/{runId}/csv", async (string runId, HttpContext context, ExcavationReportStore store, CancellationToken ct) =>
        {
            var run = await store.GetAsync((WorkstationScope)context.Items["workstation.scope"]!, runId, ct);
            return Results.File(Encoding.UTF8.GetBytes(ShiftReporting.ToCsv(run.Report)), "text/csv; charset=utf-8", "shift-" + runId[..12] + ".csv");
        });
    }
}
