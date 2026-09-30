namespace SmartMetrix.TriggerService;

public static class TriggerEndpoints
{
    public static IEndpointRouteBuilder MapTriggerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/trigger/evaluate", async (
            TriggerSnapshot snapshot,
            TriggerCoordinator coordinator,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await coordinator.EvaluateAsync(snapshot, cancellationToken);
                return result.Decision.Accepted ? Results.Accepted(value: result) : Results.Ok(result);
            }
            catch (TriggerPublicationException error)
            {
                return Results.Problem(error.Message, statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "CapturePublicationUnconfirmed",
                        ["eventId"] = error.EventId,
                        ["measurementId"] = error.MeasurementId,
                        ["expiresAt"] = error.ExpiresAt,
                        ["deliveryUncertain"] = true
                    });
            }
        });
        return endpoints;
    }
}
