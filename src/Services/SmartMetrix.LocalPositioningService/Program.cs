using SmartMetrix.LocalPositioningService;
using SmartMetrix.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.Configure<PositioningOptions>(builder.Configuration.GetSection(PositioningOptions.SectionName));
builder.Services.AddSingleton<TransformRegistry>();
builder.Services.AddSingleton<PoseResolver>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

app.MapPost("/api/transforms", (TransformDefinition transform, TransformRegistry registry) =>
    Results.Created($"/api/transforms/{transform.ExcavatorId}/{transform.Version}", registry.Add(transform)));
app.MapPost("/api/poses/resolve", (PoseRequest request, PoseResolver resolver) => Results.Ok(resolver.Resolve(request)));

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.StatusCode = error switch
    {
        PoseUnavailableException => StatusCodes.Status422UnprocessableEntity,
        KeyNotFoundException => StatusCodes.Status404NotFound,
        ArgumentException => StatusCodes.Status400BadRequest,
        InvalidOperationException => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError
    };
    await context.Response.WriteAsJsonAsync(new { code = (error as PoseUnavailableException)?.Code ?? "PositioningError", error = error?.Message });
}));

app.Run();

public partial class Program;
