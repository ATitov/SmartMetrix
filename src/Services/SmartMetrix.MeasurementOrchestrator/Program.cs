using SmartMetrix.Domain;
using SmartMetrix.MeasurementOrchestrator;
using SmartMetrix.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.Configure<MeasurementWorkflowOptions>(builder.Configuration.GetSection(MeasurementWorkflowOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IMeasurementStore, JsonMeasurementStore>();
builder.Services.AddSingleton<MeasurementWorkflow>();
builder.Services.AddHostedService<MeasurementRecoveryService>();
builder.Services.AddHostedService<DemoMeasurementSeeder>();
builder.Services.AddHostedService<DemoPipelineService>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

app.MapPost("/measurements", async (StartMeasurementRequest request, MeasurementWorkflow workflow, CancellationToken ct) =>
{
    var commandId = request.CommandId ?? Guid.CreateVersion7();
    var result = await workflow.StartAsync(commandId, request.MeasurementId, request.ExcavatorId,
        request.CoordinateSystemId, request.Reason, ct);
    return Results.Created($"/measurements/{result.Id}", result);
});

app.MapGet("/measurements/{id:guid}", async (Guid id, IMeasurementStore store, CancellationToken ct) =>
    await store.GetAsync(id, ct) is { } measurement ? Results.Ok(measurement) : Results.NotFound());

app.MapGet("/measurements", (int? limit, bool? active, IMeasurementStore store, CancellationToken ct) =>
    store.GetRecentAsync(limit ?? 50, active ?? false, ct));

app.MapPost("/measurements/{id:guid}/cancel", async (Guid id, WorkflowCommand request, MeasurementWorkflow workflow, CancellationToken ct) =>
    Results.Ok(await workflow.CancelAsync(id, request.CommandId, request.Reason, request.ExpectedVersion, ct)));

app.MapPost("/measurements/{id:guid}/retry", async (Guid id, WorkflowCommand request, MeasurementWorkflow workflow, CancellationToken ct) =>
    Results.Ok(await workflow.RetryAsync(id, request.CommandId, request.Reason, request.ExpectedVersion, ct)));

app.MapPost("/measurements/{id:guid}/transition", async (Guid id, TransitionRequest request, MeasurementWorkflow workflow, CancellationToken ct) =>
    Results.Ok(await workflow.TransitionAsync(id, request.CommandId, request.Target, request.Reason, request.ExpectedVersion, ct)));

app.Run();

public partial class Program;
