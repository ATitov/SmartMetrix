using System.ComponentModel.DataAnnotations;

namespace SmartMetrix.ServiceDefaults;

public sealed class SmartMetrixServiceOptions
{
    public const string SectionName = "SmartMetrix";

    [Required]
    [RegularExpression("^SmartMetrix\\.[A-Za-z0-9.]+$")]
    public string ServiceName { get; set; } = string.Empty;

    [Range(1, 600)]
    public int HttpClientTimeoutSeconds { get; set; } = 30;

    [Range(1, 300)]
    public int ShutdownTimeoutSeconds { get; set; } = 30;

    public TimeSpan HttpClientTimeout => TimeSpan.FromSeconds(HttpClientTimeoutSeconds);
}
