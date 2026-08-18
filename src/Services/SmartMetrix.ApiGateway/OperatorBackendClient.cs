using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SmartMetrix.ApiGateway;

public sealed class OperatorBackendClient(HttpClient httpClient, IOptions<OperatorApiOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OperatorApiOptions _options = options.Value;

    public async Task<SystemStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.UtcNow;
        var checks = await Task.WhenAll(_options.Components.Select(component => CheckAsync(component, checkedAt, cancellationToken)));
        var configured = checks.Length > 0 && checks.All(item => item.State != "NotConfigured") &&
                         TryGetOrchestrator(out _);
        var active = configured ? await GetActiveMeasurementIdAsync(cancellationToken) : null;
        var state = !configured ? "NotConfigured" : checks.All(item => item.State == "Ready") ? "Ready" : "Degraded";
        return new SystemStatus(state, configured, checkedAt, checks, active);
    }

    public async Task<IResult> GetMeasurementsAsync(CancellationToken cancellationToken)
    {
        if (!TryGetOrchestrator(out var baseUri)) return NotConfigured();
        using var response = await httpClient.GetAsync(new Uri(baseUri, "measurements"), cancellationToken);
        return await OperatorResults.FromUpstreamAsync(response, cancellationToken);
    }

    public async Task<IResult> GetMeasurementAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetOrchestrator(out var baseUri)) return NotConfigured();
        using var response = await httpClient.GetAsync(new Uri(baseUri, $"measurements/{id}"), cancellationToken);
        return await OperatorResults.FromUpstreamAsync(response, cancellationToken);
    }

    public Task<HttpResponseMessage> StartAsync(StartOperatorMeasurement request, CancellationToken cancellationToken)
    {
        if (!TryGetOrchestrator(out var baseUri)) return Task.FromResult(NotConfiguredResponse());
        var payload = new
        {
            commandId = Guid.CreateVersion7(),
            request.MeasurementId,
            request.ExcavatorId,
            request.CoordinateSystemId,
            request.Reason
        };
        return httpClient.PostAsJsonAsync(new Uri(baseUri, "measurements"), payload, JsonOptions, cancellationToken);
    }

    public Task<HttpResponseMessage> CommandAsync(Guid id, string command, OperatorCommand request, CancellationToken cancellationToken)
    {
        if (!TryGetOrchestrator(out var baseUri)) return Task.FromResult(NotConfiguredResponse());
        return httpClient.PostAsJsonAsync(new Uri(baseUri, $"measurements/{id}/{command}"),
            new { request.CommandId, request.ExpectedVersion, request.Reason }, JsonOptions, cancellationToken);
    }

    private async Task<ComponentStatus> CheckAsync(MonitoredComponent component, DateTimeOffset checkedAt, CancellationToken ct)
    {
        if (!Uri.TryCreate(component.ReadyUrl, UriKind.Absolute, out var uri))
            return new ComponentStatus(component.Name, component.Kind, "NotConfigured", checkedAt, null);
        try
        {
            using var response = await httpClient.GetAsync(uri, ct);
            return new ComponentStatus(component.Name, component.Kind,
                response.IsSuccessStatusCode ? "Ready" : "Unavailable", checkedAt,
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (HttpRequestException exception)
        {
            return new ComponentStatus(component.Name, component.Kind, "Unavailable", checkedAt, exception.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ComponentStatus(component.Name, component.Kind, "Unavailable", checkedAt, "Timed out");
        }
    }

    private async Task<string?> GetActiveMeasurementIdAsync(CancellationToken cancellationToken)
    {
        if (!TryGetOrchestrator(out var baseUri)) return null;
        try
        {
            using var response = await httpClient.GetAsync(new Uri(baseUri, "measurements?active=true&limit=1"), cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.GetArrayLength() > 0
                ? document.RootElement[0].GetProperty("id").GetString() : null;
        }
        catch (HttpRequestException) { return null; }
    }

    private bool TryGetOrchestrator(out Uri uri) => Uri.TryCreate(_options.OrchestratorUrl, UriKind.Absolute, out uri!);
    private static IResult NotConfigured() => Results.Problem("Measurement orchestrator is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable,
        extensions: new Dictionary<string, object?> { ["code"] = "NotConfigured" });
    private static HttpResponseMessage NotConfiguredResponse() => new(HttpStatusCode.ServiceUnavailable)
    {
        Content = JsonContent.Create(new { title = "Measurement orchestrator is not configured.", code = "NotConfigured" })
    };
}

public static class OperatorResults
{
    public static async Task<IResult> FromUpstreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
        return Results.Content(body, contentType, statusCode: (int)response.StatusCode);
    }
}
