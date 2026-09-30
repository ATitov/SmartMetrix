using SmartMetrix.ServiceDefaults;
using SmartMetrix.SegmentationService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.AddRuntimeSettings<SegmentationOptions>(SegmentationOptions.SectionName, "MinimumConfidence");
builder.Services.AddOptions<SegmentationOptions>()
    .Bind(builder.Configuration.GetSection(SegmentationOptions.SectionName))
    .Validate(options => options.Backend is "Deterministic" or "StoneVision", "Unknown segmentation backend.")
    .Validate(options => builder.Configuration["SmartMetrix:DeploymentProfile"] != "Production" || options.Backend == "StoneVision",
        "Production requires a real segmentation backend, including when Backend is omitted.")
    .Validate(options => double.IsFinite(options.MinimumConfidence) && options.MinimumConfidence is >= 0 and <= 1, "Invalid confidence threshold.")
    .Validate(options => string.IsNullOrWhiteSpace(options.CrackBaseUrl) || (options.Backend == "StoneVision" &&
        Uri.TryCreate(options.CrackBaseUrl, UriKind.Absolute, out var crackUri) && crackUri.Scheme is "http" or "https" &&
        !string.IsNullOrWhiteSpace(options.CrackModelVersion) && options.CrackWeightsSha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit)),
        "Crack inference requires StoneVision, an HTTP endpoint, model version and SHA256 of weights.")
    .Validate(options => options.Backend != "StoneVision" ||
        (Uri.TryCreate(options.StoneVisionBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
         options.StoneVisionTimeoutSeconds > 0 && !string.IsNullOrWhiteSpace(options.ModelVersion)), "Invalid StoneVision configuration.")
    .Validate(options => options.InputWidth > 0 && options.InputHeight > 0, "Model input size must be positive.")
    .Validate(options => options.TileOverlap >= 0 && options.TileOverlap < Math.Min(options.InputWidth, options.InputHeight), "Tile overlap is invalid.")
    .ValidateOnStart();
builder.Services.AddHttpClient<ISegmentationArtifactStore, HttpSegmentationArtifactStore>();
if (builder.Configuration["Segmentation:Backend"] == "StoneVision")
{
    builder.Services.AddHttpClient<StoneVisionClient>((services, client) =>
    {
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SegmentationOptions>>().Value;
        client.BaseAddress = new Uri(options.StoneVisionBaseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(options.StoneVisionTimeoutSeconds);
    });
    builder.Services.AddTransient<ISegmentationProcessor>(services => new StoneVisionProcessor(
        services.GetRequiredService<StoneVisionClient>(), services.GetRequiredService<ISegmentationArtifactStore>(),
        Microsoft.Extensions.Options.Options.Create(services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<SegmentationOptions>>().CurrentValue),
        services.GetService<CrackInferenceClient>()));
    if (!string.IsNullOrWhiteSpace(builder.Configuration["Segmentation:CrackBaseUrl"]))
    {
        builder.Services.AddHttpClient<CrackInferenceClient>((services, client) =>
        {
            var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SegmentationOptions>>().Value;
            client.BaseAddress = new Uri(options.CrackBaseUrl!.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(options.StoneVisionTimeoutSeconds);
        });
        builder.Services.AddHealthChecks().AddCheck<CrackInferenceHealthCheck>("crack-inference", tags: ["ready"]);
    }
    builder.Services.AddHealthChecks().AddCheck<StoneVisionHealthCheck>("stonevision", tags: ["ready"]);
}
else
{
    builder.Services.AddSingleton<ISegmentationBackend, DeterministicSegmentationBackend>();
    builder.Services.AddTransient<ISegmentationProcessor>(services => new SegmentationProcessor(
        services.GetRequiredService<ISegmentationBackend>(), services.GetRequiredService<ISegmentationArtifactStore>(),
        Microsoft.Extensions.Options.Options.Create(services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<SegmentationOptions>>().CurrentValue)));
    builder.Services.AddHostedService<ModelWarmupService>();
}

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();
app.MapRuntimeSettings<SegmentationOptions>();
app.MapGet("/v1/segmentation/capabilities", (Microsoft.Extensions.Options.IOptions<SegmentationOptions> configured) =>
{
    var options = configured.Value;
    var stoneVision = options.Backend == "StoneVision";
    return Results.Ok(new
    {
        schemaVersion = 2,
        backend = options.Backend,
        isTestData = !stoneVision,
        executesModel = stoneVision,
        supportsInstances = stoneVision,
        supportsDetectionParameters = stoneVision,
        supportsCracks = stoneVision && !string.IsNullOrWhiteSpace(options.CrackBaseUrl),
        maximumPixels = 30_000_000,
        pixelFormat = "RGB8",
        instanceMaskEncoding = "COCO uncompressed RLE",
        defaultDetection = stoneVision ? new DetectionParameters() : null,
        provenance = new SegmentationProvenance(options.Backend, options.ModelVersion,
            stoneVision ? options.StoneVisionVersion : null, stoneVision ? options.WeightsVersion : null)
    });
});
app.MapPost("/v1/measurements/{measurementId:guid}/segmentation", async (
    Guid measurementId, SegmentationRequest request, ISegmentationProcessor processor, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await processor.ProcessAsync(measurementId, request, cancellationToken)); }
    catch (ArgumentException error) { return Results.Problem(statusCode: 400, title: "InvalidSegmentationRequest", detail: error.Message); }
    catch (InvalidDataException error) { return Results.Problem(statusCode: 502, title: "InvalidSegmentationResponse", detail: error.Message); }
    catch (System.Text.Json.JsonException) { return Results.Problem(statusCode: 502, title: "InvalidSegmentationResponse", detail: "A dependency returned invalid JSON."); }
    catch (HttpRequestException) { return Results.Problem(statusCode: 502, title: "SegmentationDependencyFailure", detail: "An inference or artifact storage request failed."); }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    { return Results.Problem(statusCode: 504, title: "SegmentationDependencyTimeout", detail: "An inference or artifact storage request timed out."); }
});

app.Run();

public partial class Program;
