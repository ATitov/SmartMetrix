using SmartMetrix.ServiceDefaults;
using SmartMetrix.DepthService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.AddOptions<DepthOptions>().Bind(builder.Configuration.GetSection(DepthOptions.SectionName)).ValidateOnStart();
builder.Services.AddHttpClient<IDepthArtifactStore, HttpDepthArtifactStore>();
builder.Services.AddSingleton<CpuStereoBackend>();
builder.Services.AddSingleton<NativeStereoBackend>();
builder.Services.AddSingleton<IStereoBackend>(services =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DepthOptions>>().Value;
    return options.Backend.Equals("Cpu", StringComparison.OrdinalIgnoreCase)
        ? services.GetRequiredService<CpuStereoBackend>()
        : options.Backend.Equals("Native", StringComparison.OrdinalIgnoreCase)
            ? services.GetRequiredService<NativeStereoBackend>()
            : throw new StereoBackendNotConfiguredException($"Unknown stereo backend '{options.Backend}'.");
});
builder.Services.AddSingleton<DepthReconstructor>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();
app.MapPost("/v1/measurements/{measurementId:guid}/reconstruction", async (
    Guid measurementId, ReconstructionRequest request, DepthReconstructor reconstructor, CancellationToken cancellationToken) =>
    Results.Ok(await reconstructor.ReconstructAsync(measurementId, request, cancellationToken)));

app.Run();

public partial class Program;
