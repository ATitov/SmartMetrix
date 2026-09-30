using SmartMetrix.ServiceDefaults;
using SmartMetrix.DepthService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.AddRuntimeSettings<DepthOptions>(DepthOptions.SectionName, "MinimumDisparity", "MaximumDisparity", "MatchRadius",
    "LeftRightTolerancePixels", "MinimumSpeckleSize", "MinimumConfidence", "UniquenessRatio", "NearDistanceMetres", "FarDistanceMetres");
builder.Services.AddOptions<DepthOptions>().Bind(builder.Configuration.GetSection(DepthOptions.SectionName))
    .Validate(x => x.IsValid(), "Invalid stereo configuration.").ValidateOnStart();
builder.Services.AddHttpClient<IDepthArtifactStore, HttpDepthArtifactStore>();
builder.Services.AddTransient<IStereoBackend>(services =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<DepthOptions>>().CurrentValue;
    var snapshot = Microsoft.Extensions.Options.Options.Create(options);
    return options.Backend.Equals("Cpu", StringComparison.OrdinalIgnoreCase)
        ? new CpuStereoBackend(snapshot)
        : options.Backend.Equals("Native", StringComparison.OrdinalIgnoreCase)
            ? new NativeStereoBackend(snapshot)
            : throw new StereoBackendNotConfiguredException($"Unknown stereo backend '{options.Backend}'.");
});
builder.Services.AddTransient(services =>
{
    var snapshot = Microsoft.Extensions.Options.Options.Create(
        services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<DepthOptions>>().CurrentValue);
    IStereoBackend backend = snapshot.Value.Backend == "Cpu" ? new CpuStereoBackend(snapshot) : new NativeStereoBackend(snapshot);
    return new DepthReconstructor(backend, services.GetRequiredService<IDepthArtifactStore>(), snapshot);
});
builder.Services.AddHealthChecks().AddCheck<DepthBackendHealthCheck>("depth-backend", tags: ["ready"]);

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();
app.MapRuntimeSettings<DepthOptions>();
app.MapPost("/v1/measurements/{measurementId:guid}/reconstruction", async (
    Guid measurementId, ReconstructionRequest request, DepthReconstructor reconstructor, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await reconstructor.ReconstructAsync(measurementId, request, cancellationToken)); }
    catch (RectificationException exception)
    {
        return Results.Problem(title: exception.Code, detail: exception.Message, statusCode: exception.StatusCode,
            extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
    }
    catch (ArgumentException)
    {
        return Results.Problem(title: "InvalidCalibration", detail: "Invalid reconstruction frames or calibration.", statusCode: 422,
            extensions: new Dictionary<string, object?> { ["code"] = "InvalidCalibration" });
    }
});

app.Run();

public partial class Program;
