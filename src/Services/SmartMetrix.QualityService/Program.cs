using SmartMetrix.ServiceDefaults;
using SmartMetrix.QualityService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.Configure<QualityOptions>(builder.Configuration.GetSection(QualityOptions.SectionName));
builder.Services.AddSingleton<ILensContaminationModel, HeuristicLensContaminationModel>();
builder.Services.AddSingleton<IQualityResultStore, InMemoryQualityResultStore>();
builder.Services.AddSingleton<QualityAnalyzer>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

app.MapPost("/v1/measurements/{measurementId:guid}/quality", async (
    Guid measurementId, QualityRequest request, QualityAnalyzer analyzer, CancellationToken cancellationToken) =>
    Results.Ok(await analyzer.AssessAsync(measurementId, request, cancellationToken)));

app.MapGet("/v1/measurements/{measurementId:guid}/quality", (Guid measurementId, IQualityResultStore store) =>
    store.TryGet(measurementId, out var result) ? Results.Ok(result) : Results.NotFound());

app.Run();

public partial class Program;
