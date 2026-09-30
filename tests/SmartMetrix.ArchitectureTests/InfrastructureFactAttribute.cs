namespace SmartMetrix.ArchitectureTests;

public sealed class InfrastructureFactAttribute : FactAttribute
{
    public InfrastructureFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SMARTMETRIX_RUN_INTEGRATION_TESTS"),
                "true", StringComparison.OrdinalIgnoreCase))
            Skip = "Requires Docker and SMARTMETRIX_RUN_INTEGRATION_TESTS=true.";
    }
}
