using SmartMetrix.Domain;
using SmartMetrix.MeasurementOrchestrator;
using SmartMetrix.ServiceDefaults;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.Configure<MeasurementWorkflowOptions>(builder.Configuration.GetSection(MeasurementWorkflowOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IMeasurementStore, JsonMeasurementStore>();
builder.Services.AddSingleton<MeasurementWorkflow>();
builder.Services.AddHostedService<MeasurementRecoveryService>();
builder.Services.AddHostedService<DemoMeasurementSeeder>();
builder.Services.AddHostedService<DemoPipelineService>();
builder.Services.AddOptions<PipelineOptions>().Bind(builder.Configuration.GetSection(PipelineOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddHttpClient<PipelineTransport>((services, client) =>
    client.Timeout = TimeSpan.FromSeconds(services.GetRequiredService<Microsoft.Extensions.Options.IOptions<PipelineOptions>>().Value.RequestTimeoutSeconds));
builder.Services.AddSingleton<ProcessingPipeline>();
builder.Services.AddHostedService<MeasurementPipelineWorker>();
builder.Services.AddExceptionHandler<WorkflowExceptionHandler>();
builder.Services.AddHealthChecks().AddCheck<PipelineHealthCheck>("pipeline-configuration", tags: ["ready"]);

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

app.MapPost("/measurements", async (StartMeasurementRequest request, MeasurementWorkflow workflow,
    IOptions<PipelineOptions> pipeline, IOptions<MeasurementWorkflowOptions> workflowOptions, CancellationToken ct) =>
{
    if (PipelineHealthCheck.ConfigurationError(pipeline.Value, workflowOptions.Value) is { } error)
        return Results.Problem(error, statusCode: 503, extensions: new Dictionary<string, object?> { ["code"] = "NotConfigured" });
    var commandId = request.CommandId ?? Guid.CreateVersion7();
    var result = await workflow.StartAsync(commandId, request.MeasurementId, request.ExcavatorId,
        request.CoordinateSystemId, request.Reason, ct);
    return Results.Created($"/measurements/{result.Id}", result);
});

app.MapGet("/measurements/{id:guid}", async (Guid id, IMeasurementStore store, CancellationToken ct) =>
    await store.GetAsync(id, ct) is { } measurement ? Results.Ok(measurement) : Results.NotFound());

app.MapGet("/measurements", (int? limit, bool? active, IMeasurementStore store, CancellationToken ct) =>
    store.GetRecentAsync(limit ?? 50, active ?? false, ct));

app.MapGet("/measurements/{id:guid}/stages/{stage}", async (Guid id, string stage,
    IMeasurementStore store, PipelineTransport transport, CancellationToken ct) =>
{
    var process = await store.GetAsync(id, ct);
    if (process?.Pipeline is not { } pipeline || !pipeline.Stages.Any(x => x.Name == stage)) return Results.NotFound();
    var result = await transport.ReadStageAsync(pipeline.RunId, stage, id, ct);
    return result is { } value ? Results.Ok(value) : Results.NotFound();
});

app.MapPost("/measurements/{id:guid}/cancel", async (Guid id, WorkflowCommand request, MeasurementWorkflow workflow, CancellationToken ct) =>
    Results.Ok(await workflow.CancelAsync(id, request.CommandId, request.Reason, request.ExpectedVersion, ct)));

app.MapPost("/measurements/{id:guid}/retry", async (Guid id, WorkflowCommand request, MeasurementWorkflow workflow, CancellationToken ct) =>
    Results.Ok(await workflow.RetryAsync(id, request.CommandId, request.Reason, request.ExpectedVersion, ct)));

app.MapPost("/measurements/{id:guid}/transition", async (Guid id, TransitionRequest request, MeasurementWorkflow workflow, CancellationToken ct) =>
    builder.Configuration.GetValue<bool>("MeasurementWorkflow:RunDemoPipeline")
        ? Results.Ok(await workflow.TransitionAsync(id, request.CommandId, request.Target, request.Reason, request.ExpectedVersion, ct))
        : Results.Conflict(new { code = "PipelineOwnsTransitions", detail = "Processing transitions are managed by the pipeline." }));

app.Run();

public partial class Program;
