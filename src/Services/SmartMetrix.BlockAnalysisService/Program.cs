using SmartMetrix.ServiceDefaults;
using SmartMetrix.BlockAnalysisService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.AddOptions<BlockAnalysisOptions>()
    .Bind(builder.Configuration.GetSection(BlockAnalysisOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton<BlockAnalyzer>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();
app.MapPost("/v1/measurements/{measurementId:guid}/block-analysis", (Guid measurementId,
    BlockAnalysisRequest request, BlockAnalyzer analyzer) => Results.Ok(analyzer.Analyze(measurementId, request)));

app.Run();

public partial class Program;
