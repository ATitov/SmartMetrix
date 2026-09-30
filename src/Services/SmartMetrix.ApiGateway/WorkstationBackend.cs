using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartMetrix.ApiGateway;

public sealed class WorkstationBackend(HttpClient http)
{
    private static readonly string[] LocationFields = ["uri", "url", "path", "secret", "password", "token"];
    public async Task<JsonNode> SendAsync(WorkstationScope scope, string service, HttpMethod method,
        string path, object? body, string actor, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(http.Timeout);
        var token = timeout.Token;
        if (!scope.Services.TryGetValue(service, out var address) ||
            !Uri.TryCreate(address.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https")) throw new WorkstationApiException(503, "NotConfigured");
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.Add("X-Actor", actor);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
                throw new WorkstationApiException((int)response.StatusCode switch { 400 => 400, 404 => 404, 409 => 409, 422 => 422, _ => 502 }, "DependencyRejected");
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return new JsonObject();
            if (path == "ready") return new JsonObject { ["state"] = "Ready" };
            // Bounded JSON responses; large binary artifacts need a separate streaming contract.
            await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, token);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) ?? new JsonObject();
        }
        catch (HttpRequestException) { throw new WorkstationApiException(502, "DependencyUnavailable"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new WorkstationApiException(504, "DependencyTimeout"); }
        catch (JsonException) { throw new WorkstationApiException(502, "InvalidDependencyResponse"); }
    }

    public Task<JsonNode> GetAsync(WorkstationScope scope, string service, string path, CancellationToken ct) =>
        SendAsync(scope, service, HttpMethod.Get, path, null, "gateway", ct);

    public async Task<JsonNode> MeasurementAsync(WorkstationScope scope, Guid id, CancellationToken ct)
    {
        var value = await GetAsync(scope, "orchestrator", $"measurements/{id}", ct);
        if (value["excavatorId"]?.GetValue<string>() != scope.ExcavatorId ||
            value["coordinateSystemId"]?.GetValue<string>() != scope.CoordinateSystemId)
            throw new WorkstationApiException(404, "MeasurementNotFound");
        return value;
    }

    public static JsonNode PublicMeasurement(JsonNode value)
    {
        var result = new JsonObject();
        string[] fields = ["id", "excavatorId", "coordinateSystemId", "status", "version", "requestedAt", "updatedAt",
            "d10", "d20", "d50", "d80", "d90", "d95", "confidence", "blockCount", "oversizeFraction", "coverage", "algorithmVersion", "isTestData"];
        foreach (var key in fields) result[key] = value[key]?.DeepClone();
        result["stages"] = new JsonArray((value["pipeline"]?["stages"] as JsonArray ?? []).Select(stage => (JsonNode)new JsonObject
        {
            ["name"] = stage?["name"]?.DeepClone(),
            ["completedAt"] = stage?["completedAt"]?.DeepClone()
        }).ToArray());
        return result;
    }

    public static JsonNode WithoutLocations(JsonNode value)
    {
        var clone = value.DeepClone();
        Clean(clone);
        return clone;
    }

    private static void Clean(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var pair in obj.ToArray())
            {
                if (LocationFields.Any(term => pair.Key.Contains(term, StringComparison.OrdinalIgnoreCase)))
                    obj.Remove(pair.Key);
                else if (pair.Value is not null) Clean(pair.Value);
            }
        else if (node is JsonArray array)
            foreach (var item in array) if (item is not null) Clean(item);
    }
}
