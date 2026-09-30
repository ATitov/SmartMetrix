using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace SmartMetrix.ApiGateway;

public static class OperatorRoles
{
    public const string Operator = "operator";
    public const string Engineer = "engineer";
    public const string Administrator = "administrator";
    public const string Geologist = "geologist";
    public const string Surveyor = "surveyor";
    public static readonly string[] All = [Operator, Geologist, Surveyor, Engineer, Administrator];
}

public static class OperatorPolicies
{
    public const string View = "operator.view";
    public const string Command = "operator.command";
    public const string DangerousCommand = "operator.dangerous-command";
    public const string Administration = "operator.administration";
}

public sealed class OperatorApiOptions
{
    public const string SectionName = "OperatorApi";
    public string? OrchestratorUrl { get; set; }
    public List<MonitoredComponent> Components { get; set; } = [];
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.Ordinal);
    public string AuditPath { get; set; } = "data/operator-audit.jsonl";
    public string LogRoot { get; set; } = "C:\\DEPLOY_LOG";
    public string RuntimeConfigPath { get; set; } = "data/runtime-config.json";
    public string UserStorePath { get; set; } = "data/users.json";
    public string? BootstrapAdminPassword { get; set; }
    public string DataProtectionPath { get; set; } = "data/protection-keys";
}

public sealed class MonitoredComponent
{
    [Required] public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "service";
    public string? ReadyUrl { get; set; }
}

public sealed record StartOperatorMeasurement(Guid? MeasurementId, [Required] string ExcavatorId,
    [Required] string CoordinateSystemId, string? Reason, Guid? CommandId = null);
public sealed record OperatorCommand(Guid CommandId, long ExpectedVersion, string? Reason, bool Confirmed);
public sealed record ComponentStatus(string Name, string Kind, string State, DateTimeOffset CheckedAt, string? Detail);
public sealed record SystemStatus(string State, bool Configured, DateTimeOffset CheckedAt,
    IReadOnlyList<ComponentStatus> Components, string? ActiveMeasurementId);

public sealed record AuditEntry(DateTimeOffset OccurredAt, string Actor, string Role, string Action,
    Guid? MeasurementId, string? Reason, bool Succeeded, string? ScopeId = null, string? CorrelationId = null)
{
    public static AuditEntry Create(ClaimsPrincipal principal, string action, Guid? measurementId, string? reason, bool succeeded) =>
        new(DateTimeOffset.UtcNow, principal.Identity?.Name ?? "unknown",
            principal.FindFirstValue(ClaimTypes.Role) ?? "unknown", action, measurementId, reason, succeeded);
}
