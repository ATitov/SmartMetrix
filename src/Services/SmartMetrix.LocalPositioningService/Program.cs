using SmartMetrix.Persistence;
using SmartMetrix.LocalPositioningService;
using SmartMetrix.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.AddSmartMetrixPersistence("positioning");
builder.Services.Configure<PositioningOptions>(builder.Configuration.GetSection(PositioningOptions.SectionName));
builder.Services.AddSingleton<TransformRegistry>();
builder.Services.AddSingleton<PoseResolver>();
builder.Services.AddSingleton<PositioningSampleBuffer>();
builder.Services.AddOptions<PositioningStreamOptions>().Bind(builder.Configuration.GetSection("PositioningStreams"))
    .Validate(x => x.IsValid(), "Invalid positioning stream sources.").ValidateOnStart();
builder.Services.AddSingleton<PositioningStreamWorker>();
builder.Services.AddHostedService(services => services.GetRequiredService<PositioningStreamWorker>());
builder.Services.AddHealthChecks().AddCheck<PositioningStreamWorker>("positioning-streams", tags: ["ready"]);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.IncludeFields = true);

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

app.MapPost("/api/transforms", (TransformDefinition transform, TransformRegistry registry) =>
    Results.Created($"/api/transforms/{transform.ExcavatorId}/{transform.Version}", registry.Add(transform)));
app.MapGet("/api/transforms/{excavatorId}", (string excavatorId, TransformRegistry registry) => Results.Ok(registry.List(excavatorId)));
app.MapPost("/api/poses/resolve", (PoseRequest request, PoseResolver resolver) => Results.Ok(resolver.Resolve(request)));
app.MapPost("/api/positioning/{excavatorId}/samples", (string excavatorId, PositioningSample[] samples,
    PositioningSampleBuffer buffer) =>
{ buffer.Add(excavatorId, samples); return Results.Accepted(); });
app.MapGet("/api/poses/{excavatorId}", (string excavatorId, long hardwareTimestampNanoseconds,
    DateTimeOffset exposedAt, PositioningSampleBuffer buffer, PoseResolver resolver) =>
    Results.Ok(resolver.Resolve(new(excavatorId, hardwareTimestampNanoseconds, exposedAt, buffer.Get(excavatorId)))));

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
