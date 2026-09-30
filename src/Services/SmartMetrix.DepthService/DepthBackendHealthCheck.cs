using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartMetrix.DepthService;

public sealed class DepthBackendHealthCheck(IStereoBackend backend) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (backend is NativeStereoBackend native) native.CheckReadiness();
            return Task.FromResult(HealthCheckResult.Healthy(backend is NativeStereoBackend ? "OpenCV CUDA ABI 1; GPU available" : "CPU stereo"));
        }
        catch (StereoBackendNotConfiguredException) { return Task.FromResult(HealthCheckResult.Unhealthy("Native backend missing, diagnostic stub, incompatible ABI or unavailable GPU.")); }
    }
}
