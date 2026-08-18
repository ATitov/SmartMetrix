using SmartMetrix.ServiceDefaults;
using SmartMetrix.SegmentationService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.AddOptions<SegmentationOptions>()
    .Bind(builder.Configuration.GetSection(SegmentationOptions.SectionName))
    .Validate(options => options.InputWidth > 0 && options.InputHeight > 0, "Model input size must be positive.")
    .Validate(options => options.TileOverlap >= 0 && options.TileOverlap < Math.Min(options.InputWidth, options.InputHeight), "Tile overlap is invalid.")
    .ValidateOnStart();
builder.Services.AddHttpClient<ISegmentationArtifactStore, HttpSegmentationArtifactStore>();
builder.Services.AddSingleton<ISegmentationBackend, DeterministicSegmentationBackend>();
builder.Services.AddSingleton<SegmentationProcessor>();
builder.Services.AddHostedService<ModelWarmupService>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();
app.MapPost("/v1/measurements/{measurementId:guid}/segmentation", async (
    Guid measurementId, SegmentationRequest request, SegmentationProcessor processor, CancellationToken cancellationToken) =>
    Results.Ok(await processor.ProcessAsync(measurementId, request, cancellationToken)));

app.Run();

public partial class Program;
