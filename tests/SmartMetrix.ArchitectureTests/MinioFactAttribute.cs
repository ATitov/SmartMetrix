namespace SmartMetrix.ArchitectureTests;

public sealed class MinioFactAttribute : FactAttribute
{
    public MinioFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SMARTMETRIX_TEST_MINIO")) &&
            Environment.GetEnvironmentVariable("SMARTMETRIX_RUN_INTEGRATION_TESTS") != "true")
            Skip = "Requires SMARTMETRIX_TEST_MINIO and credentials or Docker integration tests.";
    }
}
