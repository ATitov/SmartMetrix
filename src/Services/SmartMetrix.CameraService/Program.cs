using SmartMetrix.Persistence;
using SmartMetrix.ServiceDefaults;
using SmartMetrix.CameraService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.AddSmartMetrixPersistence("camera");
builder.Services.AddCameraCapture(builder.Configuration);
builder.Services.AddSingleton<CaptureReceiptStore>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

// A single-camera diagnostic needs neither calibration nor StorageService.
// Only configured IDs are accepted; callers cannot supply arbitrary stream URLs.
app.MapPost("/v1/test/cameras/{cameraId}/capture", async (
    string cameraId, ICameraAdapter adapter, HttpContext context, CancellationToken ct) =>
{
    if (adapter is not RtspCameraAdapter rtsp)
        return Results.Problem(title: "NotConfigured", detail: "Select the Rtsp adapter and enable its test mode.", statusCode: 503);
    try
    {
        var frame = await rtsp.CaptureCameraAsync(cameraId, ct);
        var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{frame.Width} {frame.Height}\n255\n");
        var pgm = new byte[header.Length + frame.Payload.Length];
        header.CopyTo(pgm, 0);
        frame.Payload.CopyTo(pgm.AsMemory(header.Length));
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Is-Test-Data"] = "true";
        context.Response.Headers["X-Timestamp-Source"] = frame.TimestampSource;
        context.Response.Headers["X-Received-At"] = frame.ReceivedAt!.Value.ToString("O");
        return Results.File(pgm, "image/x-portable-graymap", $"camera-{frame.CameraId.ToLowerInvariant()}.pgm");
    }
    catch (CameraCaptureException exception)
    {
        return Results.Problem(title: exception.Code, detail: exception.Message, statusCode: exception.StatusCode,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
    }
});

app.MapPost("/v1/measurements/{measurementId:guid}/capture", async (
    Guid measurementId,
    CaptureRequest request,
    CaptureReceiptStore coordinator,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await coordinator.CaptureAsync(measurementId, request, cancellationToken));
    }
    catch (CameraCaptureException exception)
    {
        return Results.Problem(
            title: exception.Code,
            detail: exception.Message,
            statusCode: exception.StatusCode,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
    }
});

app.Run();

public partial class Program;
