using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SmartMetrix.ApiGateway;
using SmartMetrix.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.Services.AddOptions<OperatorApiOptions>()
    .Bind(builder.Configuration.GetSection(OperatorApiOptions.SectionName));
builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(OperatorPolicies.View, policy => policy.RequireRole(OperatorRoles.Operator, OperatorRoles.Engineer))
    .AddPolicy(OperatorPolicies.Command, policy => policy.RequireRole(OperatorRoles.Operator, OperatorRoles.Engineer))
    .AddPolicy(OperatorPolicies.DangerousCommand, policy => policy.RequireRole(OperatorRoles.Engineer));
builder.Services.AddHttpClient<OperatorBackendClient>();
builder.Services.AddSingleton<IAuditStore, JsonAuditStore>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.UseAuthentication();
app.UseAuthorization();
app.MapSmartMetrixDefaultEndpoints();

app.MapGet("/operator", () => Results.Content(OperatorDashboard.Html, "text/html; charset=utf-8"));

var api = app.MapGroup("/api/operator").RequireAuthorization(OperatorPolicies.View);
api.MapGet("/status", (OperatorBackendClient backend, CancellationToken ct) => backend.GetStatusAsync(ct));
api.MapGet("/measurements", (OperatorBackendClient backend, CancellationToken ct) => backend.GetMeasurementsAsync(ct));
api.MapGet("/measurements/{id:guid}", (Guid id, OperatorBackendClient backend, CancellationToken ct) =>
    backend.GetMeasurementAsync(id, ct));
api.MapGet("/audit", (IAuditStore audit, CancellationToken ct) => audit.ReadAsync(ct))
    .RequireAuthorization(OperatorPolicies.DangerousCommand);

api.MapPost("/measurements", async (StartOperatorMeasurement request, HttpContext context,
    OperatorBackendClient backend, IAuditStore audit, CancellationToken ct) =>
{
    var response = await backend.StartAsync(request, ct);
    await audit.AppendAsync(AuditEntry.Create(context.User, "measurement.start", request.MeasurementId, request.Reason,
        response.IsSuccessStatusCode), ct);
    return await OperatorResults.FromUpstreamAsync(response, ct);
}).RequireAuthorization(OperatorPolicies.Command);

api.MapPost("/measurements/{id:guid}/{command:regex(^(cancel|retry)$)}", async (Guid id, string command,
    OperatorCommand request, HttpContext context, OperatorBackendClient backend, IAuditStore audit, CancellationToken ct) =>
{
    if (!request.Confirmed)
        return Results.Problem("Explicit confirmation is required.", statusCode: StatusCodes.Status409Conflict);

    var response = await backend.CommandAsync(id, command, request, ct);
    await audit.AppendAsync(AuditEntry.Create(context.User, $"measurement.{command}", id, request.Reason,
        response.IsSuccessStatusCode), ct);
    return await OperatorResults.FromUpstreamAsync(response, ct);
}).RequireAuthorization(OperatorPolicies.DangerousCommand);

app.Run();

public partial class Program;
