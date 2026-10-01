using SmartMetrix.ServiceDefaults;
using SmartMetrix.BlockAnalysisService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
var boundaries = builder.Configuration.GetSection("BlockAnalysis:SizeClassBoundariesMillimetres").Get<double[]>() ?? [100, 300, 600];
builder.AddRuntimeSettings<BlockAnalysisOptions>(BlockAnalysisOptions.SectionName, BlockAnalysisOptions.EditableSettings
    .Concat(Enumerable.Range(0, boundaries.Length).Select(index => $"SizeClassBoundariesMillimetres:{index}")).ToArray());
builder.Services.AddOptions<BlockAnalysisOptions>()
    .Bind(builder.Configuration.GetSection(BlockAnalysisOptions.SectionName))
    .PostConfigure(options => options.WithDefaults())
    .ValidateDataAnnotations().Validate(x => x.ValidSizeClasses(), "Size class boundaries must be positive and strictly increasing.").ValidateOnStart();
builder.Services.AddTransient(services => new BlockAnalyzer(Microsoft.Extensions.Options.Options.Create(
    services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<BlockAnalysisOptions>>().CurrentValue)));

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();
app.MapRuntimeSettings<BlockAnalysisOptions>();
app.MapPost("/v1/measurements/{measurementId:guid}/block-analysis", (Guid measurementId,
    BlockAnalysisRequest request, BlockAnalyzer analyzer) => Results.Ok(analyzer.Analyze(measurementId, request)));

app.Run();

public partial class Program;
