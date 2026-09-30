using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace SmartMetrix.LocalPositioningService;

public sealed class PositioningStreamOptions
{
    public List<PositioningStreamSource> Sources { get; set; } = [];
    public int ReconnectDelaySeconds { get; set; } = 2;
    public int MaximumSilenceSeconds { get; set; } = 5;
    public string ExposureClockId { get; set; } = "";
    public bool IsValid() => ReconnectDelaySeconds is >= 1 and <= 60 && MaximumSilenceSeconds is >= 1 and <= 300 &&
        Sources.Select(x => x.SourceId).Distinct().Count() == Sources.Count && Sources.All(x =>
            !string.IsNullOrWhiteSpace(x.Host) && x.Port is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(x.ExcavatorId) &&
            !string.IsNullOrWhiteSpace(x.SourceId) && !string.IsNullOrWhiteSpace(x.ClockId) &&
            x.ClockId == ExposureClockId && Enum.IsDefined(x.SourceType));
}

public sealed record PositioningStreamSource(string Host, int Port, string ExcavatorId, PositionSourceType SourceType,
    string SourceId, string ClockId);
public sealed record PositioningWireSample(int SchemaVersion, string ClockId, PositioningSample Sample);

// Concrete transport for a device bridge; vendor packets must be converted by the bridge.
public sealed class JsonLinePositioningAdapter(PositioningStreamSource source, int maximumSilenceSeconds) : IPositioningAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { IncludeFields = true };
    public PositionSourceType SourceType => source.SourceType;

    public async IAsyncEnumerable<PositioningSample> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(maximumSilenceSeconds));
        await client.ConnectAsync(source.Host, source.Port, connectTimeout.Token);
        await using var stream = client.GetStream();
        var chunk = new byte[4096]; var line = new List<byte>();
        while (!cancellationToken.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(maximumSilenceSeconds));
            var count = await stream.ReadAsync(chunk, timeout.Token);
            if (count == 0) yield break;
            for (var i = 0; i < count; i++)
            {
                if (chunk[i] == (byte)'\n')
                {
                    var envelope = JsonSerializer.Deserialize<PositioningWireSample>(line.ToArray(), Json)
                        ?? throw new InvalidDataException("Positioning bridge returned an empty envelope.");
                    line.Clear();
                    if (envelope.SchemaVersion != 1 || envelope.ClockId != source.ClockId || envelope.Sample is null ||
                        envelope.Sample.SourceId != source.SourceId || envelope.Sample.SourceType != source.SourceType)
                        throw new InvalidDataException("Positioning source identity, clock or schema mismatch.");
                    Validate(envelope.Sample);
                    yield return envelope.Sample with { ClockId = source.ClockId, ProtocolVersion = "json-lines-v1" };
                }
                else
                {
                    if (line.Count >= 8192) throw new InvalidDataException("Positioning envelope exceeds 8192 bytes.");
                    line.Add(chunk[i]);
                }
            }
        }
    }

    public static void Validate(PositioningSample sample)
    {
        if (sample.HardwareTimestampNanoseconds < 0 || sample.PositionMetres is null && sample.Orientation is null)
            throw new InvalidDataException("Positioning sample requires a hardware timestamp and position or orientation.");
        if (sample.PositionMetres is { } p && (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) ||
            sample.Orientation is { } q && (!float.IsFinite(q.LengthSquared()) || Math.Abs(q.LengthSquared() - 1) > .001))
            throw new InvalidDataException("Positioning geometry must be finite and orientation normalized.");
        if (sample.Covariance is null) throw new InvalidDataException("Positioning covariance is required.");
        try { new SmartMetrix.Contracts.CalibrationAccuracy("positioning-wire-v1", 0, sample.Covariance).Validate(); }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid positioning covariance.", error); }
    }
}

public sealed class PositioningStreamWorker(IOptions<PositioningStreamOptions> configured, PositioningSampleBuffer buffer,
    ILogger<PositioningStreamWorker> logger) : BackgroundService, IHealthCheck
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> received = new();
    private static readonly Action<ILogger, string, Exception?> LogRetry = LoggerMessage.Define<string>(LogLevel.Warning,
        new EventId(2201, "PositioningReconnect"), "Positioning source {SourceId} disconnected; retrying.");

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(configured.Value.Sources.Select(source => RunAsync(source, stoppingToken)));

    private async Task RunAsync(PositioningStreamSource source, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var adapter = new JsonLinePositioningAdapter(source, configured.Value.MaximumSilenceSeconds);
                await foreach (var sample in adapter.ReadAsync(ct))
                {
                    buffer.Add(source.ExcavatorId, [sample]);
                    received[source.SourceId] = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error) when (error is SocketException or IOException or JsonException or OperationCanceledException or ArgumentException)
            { LogRetry(logger, source.SourceId, null); }
            await Task.Delay(TimeSpan.FromSeconds(configured.Value.ReconnectDelaySeconds), ct);
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(configured.Value.Sources.All(source => received.TryGetValue(source.SourceId, out var last) &&
            DateTimeOffset.UtcNow - last <= TimeSpan.FromSeconds(configured.Value.MaximumSilenceSeconds))
            ? HealthCheckResult.Healthy(configured.Value.Sources.Count == 0 ? "HTTP sample ingestion; no device bridges configured." : "Configured positioning bridges are receiving samples.")
            : HealthCheckResult.Unhealthy("A configured positioning bridge has no recent samples."));
}
