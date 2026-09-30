using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartMetrix.CameraService;

public sealed class CameraBackendHealthCheck(ICameraAdapter adapter) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (adapter is ArenaCameraAdapter arena) arena.CheckReadiness();
            return Task.FromResult(HealthCheckResult.Healthy(adapter.Name == "Arena" ? "Arena SDK ABI 2; configured cameras opened" : "Test capture backend: " + adapter.Name));
        }
        catch (CameraCaptureException error) { return Task.FromResult(HealthCheckResult.Unhealthy(error.Code)); }
    }
}
