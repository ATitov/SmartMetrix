using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SmartMetrix.ServiceDefaults;

public sealed class CorrelationContextMiddleware(
    RequestDelegate next,
    ILogger<CorrelationContextMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var requestedCorrelationId = context.Request.Headers[CorrelationContext.CorrelationHeader].ToString();
        var correlationId = CorrelationContext.IsValid(requestedCorrelationId)
            ? requestedCorrelationId
            : CorrelationContext.CreateCorrelationId();

        var requestedMeasurementId = context.Request.Headers[CorrelationContext.MeasurementHeader].ToString();
        var measurementId = CorrelationContext.IsValid(requestedMeasurementId)
            ? requestedMeasurementId
            : null;

        context.Response.Headers[CorrelationContext.CorrelationHeader] = correlationId;
        Activity.Current?.SetTag("smartmetrix.correlation_id", correlationId);
        Activity.Current?.SetTag("smartmetrix.measurement_id", measurementId);

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId,
            ["MeasurementId"] = measurementId
        });

        await next(context);
    }
}
