using System.Diagnostics;

namespace SmartMetrix.ServiceDefaults;

public static class SmartMetrixTelemetry
{
    public const string ActivitySourceName = "SmartMetrix";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static Activity? StartEventProcessing(
        string eventName,
        string eventId,
        string? correlationId = null,
        string? measurementId = null)
    {
        var activity = ActivitySource.StartActivity(
            $"event process {eventName}",
            ActivityKind.Consumer);

        activity?.SetTag("messaging.operation.name", "process");
        activity?.SetTag("messaging.message.type", eventName);
        activity?.SetTag("messaging.message.id", eventId);
        activity?.SetTag("smartmetrix.correlation_id", correlationId);
        activity?.SetTag("smartmetrix.measurement_id", measurementId);
        return activity;
    }
}
