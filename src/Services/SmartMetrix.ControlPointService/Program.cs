using SmartMetrix.ServiceDefaults;
using SmartMetrix.ControlPointService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ControlPointRegistry>();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

app.MapGet("/control-points", (ControlPointRegistry registry, bool activeOnly = false) => registry.List(activeOnly));
app.MapGet("/control-points/{pointId}", (string pointId, ControlPointRegistry registry) => Results.Ok(registry.Get(pointId)));
app.MapGet("/control-points/{pointId}/history", (string pointId, ControlPointRegistry registry) => Results.Ok(registry.History(pointId)));
app.MapGet("/control-points/{pointId}/audit", (string pointId, ControlPointRegistry registry) => Results.Ok(registry.AuditLog(pointId)));
app.MapPost("/control-points", (CreateControlPointRequest request, ControlPointRegistry registry) =>
{
    var result = registry.Create(request, RequestActor());
    return Results.Created($"/control-points/{Uri.EscapeDataString(result.PointId)}", result);
});
app.MapPut("/control-points/{pointId}", (string pointId, UpdateControlPointRequest request, ControlPointRegistry registry) => Results.Ok(registry.Update(pointId, request, RequestActor())));
app.MapPost("/control-points/{pointId}/observations", (string pointId, AddSurveyObservationRequest request, ControlPointRegistry registry) => Results.Ok(registry.AddObservation(pointId, request, RequestActor())));
app.MapPost("/control-points/{pointId}/usages/{measurementId:guid}", (string pointId, Guid measurementId, ControlPointRegistry registry) => Results.Ok(registry.RecordUsage(pointId, measurementId, RequestActor())));
app.MapDelete("/control-points/{pointId}", (string pointId, ControlPointRegistry registry) => { registry.Delete(pointId, RequestActor()); return Results.NoContent(); });
app.MapPost("/control-points/import/csv", async (HttpRequest request, ControlPointRegistry registry, CancellationToken ct) => Results.Ok(registry.ImportCsv(await new StreamReader(request.Body).ReadToEndAsync(ct), RequestActor())));
app.MapPost("/control-points/import/geojson", async (HttpRequest request, ControlPointRegistry registry, CancellationToken ct) => Results.Ok(registry.ImportGeoJson(await new StreamReader(request.Body).ReadToEndAsync(ct), RequestActor())));
app.MapGet("/control-points/export/csv", (ControlPointRegistry registry) => Results.Text(registry.ExportCsv(), "text/csv"));
app.MapGet("/control-points/export/geojson", (ControlPointRegistry registry) => Results.Text(registry.ExportGeoJson(), "application/geo+json"));

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error switch
    {
        KeyNotFoundException => StatusCodes.Status404NotFound,
        ControlPointConflictException => StatusCodes.Status409Conflict,
        ControlPointValidationException => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError
    };
    await context.Response.WriteAsJsonAsync(new { error = error?.Message ?? "Unexpected error." });
}));

string RequestActor() => app.Services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Request.Headers["X-Actor"].FirstOrDefault() ?? "system";

app.Run();

public partial class Program;
