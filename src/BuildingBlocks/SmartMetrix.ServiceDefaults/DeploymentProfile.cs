using Microsoft.Extensions.Configuration;

namespace SmartMetrix.ServiceDefaults;

public static class DeploymentProfile
{
    public static void Validate(IConfiguration configuration)
    {
        var profile = configuration["SmartMetrix:DeploymentProfile"] ?? "Local";
        if (profile is not ("Local" or "Test" or "Production"))
            throw new InvalidOperationException("DeploymentProfile must be Local, Test or Production.");
        if (profile != "Production") return;
        if (string.Equals(configuration["Camera:Adapter"], "Simulator", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuration["Camera:Adapter"], "Rtsp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuration["Segmentation:Backend"], "Deterministic", StringComparison.OrdinalIgnoreCase) ||
            configuration.GetValue<bool>("MeasurementWorkflow:RunDemoPipeline") ||
            configuration.GetValue<bool>("MeasurementWorkflow:SeedDemoData"))
            throw new InvalidOperationException("Production deployment rejects test camera, segmentation and demo pipeline backends.");
    }
}
