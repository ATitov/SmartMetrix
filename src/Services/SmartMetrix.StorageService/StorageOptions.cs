using System.ComponentModel.DataAnnotations;

namespace SmartMetrix.StorageService;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    [Required, Url]
    public string Endpoint { get; init; } = "http://localhost:9000";

    [Required]
    public string AccessKey { get; init; } = "smartmetrix";

    [Required]
    public string SecretKey { get; init; } = "smartmetrix-dev-secret";

    [Required, RegularExpression("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
    public string Bucket { get; init; } = "smartmetrix-artifacts";

    [Required]
    public string Region { get; init; } = "us-east-1";

    [Range(1, 36500)]
    public int RetentionDays { get; init; } = 365;

    [Range(1, 365)]
    public int IncompleteUploadRetentionDays { get; init; } = 1;

    [Range(1, 86400)]
    public int PresignedUrlLifetimeSeconds { get; init; } = 900;
}
