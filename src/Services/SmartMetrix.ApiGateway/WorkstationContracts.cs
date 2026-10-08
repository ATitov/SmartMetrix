using System.Security.Claims;
using System.Text.RegularExpressions;

namespace SmartMetrix.ApiGateway;

public sealed class WorkstationOptions
{
    public const string Section = "Workstations";
    public List<WorkstationScope> Scopes { get; set; } = [];
    public Dictionary<string, string[]> ApiKeyScopes { get; set; } = new(StringComparer.Ordinal);
    public string ReviewPath { get; set; } = "data/workstation-reviews.json";
    public string BackupDirectory { get; set; } = "data/backups";
    public int TimeoutSeconds { get; set; } = 15;
}

public sealed class WorkstationScope
{
    public string Id { get; set; } = "";
    public string SiteId { get; set; } = "";
    public string ExcavatorId { get; set; } = "";
    public string RigId { get; set; } = "";
    public string CoordinateSystemId { get; set; } = "";
    public Dictionary<string, string> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class WorkstationIdentity
{
    public static bool ResourceIdentifier(string? value) => value is not null &&
        Regex.IsMatch(value, "^[\\p{L}\\p{N}][\\p{L}\\p{N}_.:-]{0,127}$", RegexOptions.CultureInvariant);

    public const string ScopeClaim = "smartmetrix:scope";
    public static bool Identifier(string? value) => value is not null &&
        Regex.IsMatch(value, "^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}$", RegexOptions.CultureInvariant);

    public static void ValidateAccess(string role, string[] roles, string[] scopes)
    {
        if (!OperatorRoles.All.Contains(role) || roles is null || scopes is null ||
            roles.Length > 5 || roles.Any(value => !OperatorRoles.All.Contains(value)) ||
            scopes.Length > 100 || scopes.Any(value => !Identifier(value)))
            throw new ArgumentException("Invalid roles or scope identifiers.");
    }

    public static ClaimsPrincipal Principal(UserAccount user, string scheme)
    {
        List<Claim> claims = [new(ClaimTypes.Name, user.Username), new(ClaimTypes.GivenName, user.DisplayName)];
        claims.AddRange(new[] { user.Role }.Concat(user.AdditionalRoles ?? []).Distinct().Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange((user.ScopeIds ?? []).Distinct().Select(scope => new Claim(ScopeClaim, scope)));
        return new(new ClaimsIdentity(claims, scheme));
    }

    public static bool CanAccess(ClaimsPrincipal user, string scope) => user.HasClaim(ScopeClaim, scope);
}

public sealed record WorkstationStart(Guid CommandId, Guid MeasurementId, string Reason);
public sealed record WorkstationDecision(Guid CommandId, long ExpectedVersion, string Decision, string Reason);
public sealed record WorkstationMutation(Guid CommandId, long ExpectedVersion, string Reason, bool Confirmed);
public sealed record ReviewRecord(string ScopeId, Guid MeasurementId, Guid CommandId, long ResultVersion,
    string Decision, string Reason, string Actor, DateTimeOffset OccurredAt);
public sealed class WorkstationApiException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public static class WorkstationValidation
{
    public static void Command(Guid commandId, long version, string? reason)
    {
        if (commandId == Guid.Empty || version < 0 || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 3 or > 500)
            throw new WorkstationApiException(400, "InvalidCommand");
    }
}
