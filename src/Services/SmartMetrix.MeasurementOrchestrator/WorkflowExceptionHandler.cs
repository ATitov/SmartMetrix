using Microsoft.AspNetCore.Diagnostics;

namespace SmartMetrix.MeasurementOrchestrator;

public sealed class WorkflowExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            KeyNotFoundException => 404,
            ArgumentException => 400,
            InvalidOperationException => 409,
            PipelineException => 502,
            _ => 0
        };
        if (status == 0) return false;
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new { code = exception.GetType().Name, detail = exception.Message }, cancellationToken);
        return true;
    }
}
