using SmartMetrix.ServiceDefaults;
using SmartMetrix.CameraService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.AddCameraCapture(builder.Configuration);
builder.Services.AddSingleton<CaptureReceiptStore>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

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
