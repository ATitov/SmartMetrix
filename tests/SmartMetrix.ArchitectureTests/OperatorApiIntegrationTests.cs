using Microsoft.Extensions.Options;
using SmartMetrix.ApiGateway;

namespace SmartMetrix.ArchitectureTests;

public sealed class OperatorApiIntegrationTests
{
    [Fact]
    public async Task UnconfiguredDependenciesAreReportedWithoutSyntheticValues()
    {
        var options = Options.Create(new OperatorApiOptions
        {
            Components =
            [
                new MonitoredComponent { Name = "Cameras", Kind = "camera" },
                new MonitoredComponent { Name = "Storage", Kind = "storage" },
                new MonitoredComponent { Name = "Queues", Kind = "queue" }
            ]
        });
        var client = new OperatorBackendClient(new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK))), options);

        var status = await client.GetStatusAsync(CancellationToken.None);

        Assert.False(status.Configured);
        Assert.Equal("NotConfigured", status.State);
        Assert.Null(status.ActiveMeasurementId);
        Assert.All(status.Components, component => Assert.Equal("NotConfigured", component.State));
    }

    [Fact]
    public async Task ComponentFailuresProduceDegradedStatusWithoutLeakingAddresses()
    {
        var options = Options.Create(new OperatorApiOptions
        {
            OrchestratorUrl = "http://orchestrator.internal/",
            Components =
            [
                new MonitoredComponent { Name = "Cameras", Kind = "camera", ReadyUrl = "http://camera.internal/ready" },
                new MonitoredComponent { Name = "Storage", Kind = "storage", ReadyUrl = "http://storage.internal/ready" }
            ]
        });
        var client = new OperatorBackendClient(new HttpClient(new StubHandler(request =>
            request.RequestUri!.Host == "camera.internal"
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                : new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable))), options);

        var status = await client.GetStatusAsync(CancellationToken.None);

        Assert.True(status.Configured);
        Assert.Equal("Degraded", status.State);
        Assert.DoesNotContain("internal", System.Text.Json.JsonSerializer.Serialize(status), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
