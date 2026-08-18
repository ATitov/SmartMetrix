using SmartMetrix.ServiceDefaults;
using SmartMetrix.CalibrationService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.Configure<CalibrationOptions>(builder.Configuration.GetSection(CalibrationOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CalibrationRegistry>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

var calibrations = app.MapGroup("/api/calibrations");
calibrations.MapPost("/", (CalibrationPayload payload, CalibrationRegistry registry, HttpContext context) =>
{
    var calibration = registry.Create(payload, context.User.Identity?.Name ?? "api");
    return Results.Created($"/api/calibrations/{calibration.Id}", calibration);
});
calibrations.MapGet("/", (string? rigId, CalibrationRegistry registry) => registry.List(rigId));
calibrations.MapGet("/{id:guid}", (Guid id, CalibrationRegistry registry) => registry.Get(id));
calibrations.MapPost("/{id:guid}/activate", (Guid id, ActivationRequest request, CalibrationRegistry registry) => registry.Activate(id, request.ValidFrom, request.ValidTo, request.Actor));
calibrations.MapPost("/{id:guid}/revoke", (Guid id, RevocationRequest request, CalibrationRegistry registry) => registry.Revoke(id, request.Actor, request.Reason));
calibrations.MapGet("/{id:guid}/audit", (Guid id, CalibrationRegistry registry) => registry.AuditLog(id));
calibrations.MapGet("/{id:guid}/bundle", (Guid id, CalibrationRegistry registry) => registry.Export(id));
calibrations.MapGet("/active/{rigId}", (string rigId, DateTimeOffset? at, CalibrationRegistry registry) => registry.GetActive(rigId, at ?? DateTimeOffset.UtcNow) is { } active ? Results.Ok(active) : Results.NotFound());

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error switch { KeyNotFoundException => 404, CalibrationConflictException => 409, CalibrationValidationException => 400, _ => 500 };
    await context.Response.WriteAsJsonAsync(new { error = error?.Message ?? "Unexpected error." });
}));

app.Run();

public partial class Program;
