using System.Net;
using System.Text.Json.Nodes;
using SmartMetrix.ApiGateway;

namespace SmartMetrix.ArchitectureTests;

public sealed class WorkstationBackendTests
{
    [Theory]
    [InlineData(400, 400)]
    [InlineData(404, 404)]
    [InlineData(409, 409)]
    [InlineData(422, 422)]
    [InlineData(500, 502)]
    [InlineData(302, 502)]
    [Trait("Requirement", "ARM-ERR-01")]
    public async Task UpstreamErrorsHaveStableStatusAndDoNotExposeDetails(int upstream, int expected)
    {
        using var http = new HttpClient(new ResponseHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)upstream)
        { Content = new StringContent("http://private-host/secret password=hidden") })));
        var backend = new WorkstationBackend(http);
        var error = await Assert.ThrowsAsync<WorkstationApiException>(() => backend.GetAsync(Scope(), "orchestrator", "measurements", CancellationToken.None));
        Assert.Equal(expected, error.Status);
        Assert.DoesNotContain("private-host", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "ARM-ERR-02")]
    public async Task TimeoutIsDistinctFromCallerCancellation()
    {
        using var http = new HttpClient(new ResponseHandler(async (_, ct) =>
        { await Task.Delay(TimeSpan.FromSeconds(5), ct); return new HttpResponseMessage(HttpStatusCode.OK); }))
        { Timeout = TimeSpan.FromMilliseconds(50) };
        var backend = new WorkstationBackend(http);
        var timeout = await Assert.ThrowsAsync<WorkstationApiException>(() => backend.GetAsync(Scope(), "orchestrator", "measurements", CancellationToken.None));
        Assert.Equal(504, timeout.Status);
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.GetAsync(Scope(), "orchestrator", "measurements", caller.Token));
    }

    [Fact]
    [Trait("Requirement", "ARM-ERR-03")]
    public async Task MissingServiceAndInvalidJsonNeverBecomeSyntheticSuccess()
    {
        using var http = new HttpClient(new ResponseHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") })));
        var backend = new WorkstationBackend(http);
        var missing = await Assert.ThrowsAsync<WorkstationApiException>(() => backend.GetAsync(Scope(), "camera", "ready", CancellationToken.None));
        Assert.Equal(503, missing.Status);
        var invalid = await Assert.ThrowsAsync<WorkstationApiException>(() => backend.GetAsync(Scope(), "orchestrator", "measurements", CancellationToken.None));
        Assert.Equal(502, invalid.Status);
        // Health endpoints return plain text; a successful health response is not parsed as JSON.
        Assert.Equal("Ready", (await backend.GetAsync(Scope(), "orchestrator", "ready", CancellationToken.None))["state"]!.GetValue<string>());
    }

    [Fact]
    [Trait("Requirement", "ARM-SEC-05")]
    public void PublicResultProjectionKeepsUnknownValuesAndRemovesInternalLocations()
    {
        var raw = JsonNode.Parse("""{"id":"sample","d50":null,"pipeline":{"stages":[{"name":"depth","artifactUri":"s3://internal"}]},"processedCommands":["secret"]}""")!;
        var projected = WorkstationBackend.PublicMeasurement(raw);
        Assert.Null(projected["d50"]);
        Assert.Null(projected["processedCommands"]);
        Assert.DoesNotContain("s3://", projected.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("s3://", raw.ToJsonString(), StringComparison.Ordinal);
    }

    private static WorkstationScope Scope() => new() { Services = new() { ["orchestrator"] = "http://internal/" } };
    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }
}
