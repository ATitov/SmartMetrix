using SmartMetrix.ServiceDefaults;
using SmartMetrix.QualityService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
var sceneKeys = builder.Configuration.GetSection("Quality:Scenes").GetChildren().SelectMany(scene =>
    QualityOptions.EditableThresholds
        .Select(key => $"Scenes:{scene.Key}:{key}"));
builder.AddRuntimeSettings<QualityOptions>(QualityOptions.SectionName, QualityOptions.EditableMetrics.Concat(sceneKeys).ToArray());
builder.Services.AddOptions<QualityOptions>().Bind(builder.Configuration.GetSection(QualityOptions.SectionName))
    .Validate(x => x.IsValid(), "Invalid quality profile.").ValidateOnStart();
builder.Services.AddSingleton<ILensContaminationModel, HeuristicLensContaminationModel>();
builder.Services.AddSingleton<IQualityResultStore, FileQualityResultStore>();
builder.Services.AddSingleton<QualityAnalyzer>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();
app.MapRuntimeSettings<QualityOptions>();

app.MapPost("/v1/measurements/{measurementId:guid}/quality", async (
    Guid measurementId, QualityRequest request, QualityAnalyzer analyzer, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await analyzer.AssessAsync(measurementId, request, cancellationToken)); }
    catch (ArgumentException error) { return Results.Problem(statusCode: 400, title: "InvalidQualityRequest", detail: error.Message); }
    catch (InvalidOperationException error) { return Results.Problem(statusCode: 409, title: "QualityResultConflict", detail: error.Message); }
});

app.MapGet("/v1/measurements/{measurementId:guid}/quality", (Guid measurementId, IQualityResultStore store) =>
    store.TryGet(measurementId, out var result) ? Results.Ok(result) : Results.NotFound());

app.Run();

public partial class Program;
