using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed class PipelineHealthCheck(IOptions<PipelineOptions> pipeline, IOptions<MeasurementWorkflowOptions> workflow) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var error = ConfigurationError(pipeline.Value, workflow.Value);
        return Task.FromResult(error is null
            ? HealthCheckResult.Healthy(workflow.Value.RunDemoPipeline ? "Demo pipeline" : "Service pipeline configured")
            : HealthCheckResult.Unhealthy(error));
    }

    public static string? ConfigurationError(PipelineOptions pipeline, MeasurementWorkflowOptions workflow) =>
        workflow.RunDemoPipeline ? null : !pipeline.Enabled ? "Processing pipeline is disabled." :
        string.IsNullOrWhiteSpace(pipeline.RigId) ? "Pipeline:RigId is required." : null;
}
