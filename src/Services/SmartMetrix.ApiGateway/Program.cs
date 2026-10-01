using SmartMetrix.Persistence;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using SmartMetrix.ApiGateway;
using SmartMetrix.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.AddSmartMetrixPersistence("operator");
builder.Services.AddWorkstationApi(builder.Configuration);
builder.Services.AddOptions<OperatorApiOptions>()
    .Bind(builder.Configuration.GetSection(OperatorApiOptions.SectionName));
var protectionPath = builder.Configuration[$"{OperatorApiOptions.SectionName}:DataProtectionPath"] ?? "data/protection-keys";
if (!Path.IsPathRooted(protectionPath)) protectionPath = Path.Combine(builder.Environment.ContentRootPath, protectionPath);
Directory.CreateDirectory(protectionPath);
var dataProtection = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(protectionPath))
    .SetApplicationName("SmartMetrix.ApiGateway");
if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi(true);

const string combinedScheme = "SmartMetrixAuth";
const string cookieScheme = "SmartMetrixCookie";
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = combinedScheme;
        options.DefaultChallengeScheme = combinedScheme;
    })
    .AddPolicyScheme(combinedScheme, combinedScheme, options => options.ForwardDefaultSelector = context =>
        context.Request.Headers.ContainsKey("X-API-Key") ? ApiKeyAuthenticationHandler.SchemeName : cookieScheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthentication().AddCookie(cookieScheme, options =>
{
    options.LoginPath = "/login/";
    options.AccessDeniedPath = "/forbidden/";
    options.Cookie.Name = "SmartMetrix.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Events.OnValidatePrincipal = async context =>
    {
        var users = context.HttpContext.RequestServices.GetRequiredService<UserAccountStore>();
        var user = await users.FindAsync(context.Principal?.Identity?.Name ?? "", context.HttpContext.RequestAborted);
        if (user is null || !user.Enabled || user.LockedUntil > DateTimeOffset.UtcNow)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(cookieScheme);
            return;
        }
        context.ReplacePrincipal(WorkstationIdentity.Principal(user, cookieScheme));
    };
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status403Forbidden;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(OperatorPolicies.View, policy => policy.RequireRole(OperatorRoles.Operator, OperatorRoles.Engineer, OperatorRoles.Administrator))
    .AddPolicy(OperatorPolicies.Command, policy => policy.RequireRole(OperatorRoles.Operator, OperatorRoles.Engineer, OperatorRoles.Administrator))
    .AddPolicy(OperatorPolicies.DangerousCommand, policy => policy.RequireRole(OperatorRoles.Engineer, OperatorRoles.Administrator))
    .AddPolicy(OperatorPolicies.Administration, policy => policy.RequireRole(OperatorRoles.Administrator));
builder.Services.AddHttpClient<OperatorBackendClient>();
builder.Services.AddSingleton<IAuditStore, JsonAuditStore>();
builder.Services.AddSingleton<EngineeringTools>();
builder.Services.AddSingleton<UserAccountStore>();
builder.Services.AddHostedService<UserStoreInitializer>();

var app = builder.Build();
app.UseSmartMetrixServiceDefaults();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseWorkstationSecurity();
app.MapSmartMetrixDefaultEndpoints();
app.MapWorkstationApi();

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, UserAccountStore users, CancellationToken ct) =>
{
    var user = await users.AuthenticateAsync(request.Username, request.Password, ct);
    if (user is null) return Results.Problem("Неверный логин или пароль, учетная запись отключена либо временно заблокирована.", statusCode: 401);
    await context.SignInAsync(cookieScheme, WorkstationIdentity.Principal(user, cookieScheme), new AuthenticationProperties
    {
        IsPersistent = false,
        AllowRefresh = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
    });
    return Results.Ok(new { user.Username, user.DisplayName, user.Role });
}).AllowAnonymous();
app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(cookieScheme);
    return Results.NoContent();
}).RequireAuthorization();
app.MapGet("/api/auth/me", (ClaimsPrincipal user) => Results.Ok(new
{
    Username = user.Identity?.Name,
    DisplayName = user.FindFirstValue(ClaimTypes.GivenName) ?? user.Identity?.Name,
    Role = user.FindFirstValue(ClaimTypes.Role),
    Roles = user.FindAll(ClaimTypes.Role).Select(x => x.Value).ToArray(),
    ScopeIds = user.FindAll(WorkstationIdentity.ScopeClaim).Select(x => x.Value).ToArray()
})).RequireAuthorization();

var administration = app.MapGroup("/api/admin").RequireAuthorization(OperatorPolicies.Administration);
administration.MapGet("/users", (UserAccountStore users, CancellationToken ct) => users.ListAsync(ct));
administration.MapPost("/users", async (CreateUserRequest request, UserAccountStore users, CancellationToken ct) =>
{
    try { await users.AddAsync(request, ct); return Results.Created($"/api/admin/users/{request.Username}", null); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
});
administration.MapPut("/users/{username}/enabled/{enabled:bool}", async (string username, bool enabled, UserAccountStore users, CancellationToken ct) =>
{
    try { await users.SetEnabledAsync(username, enabled, ct); return Results.NoContent(); }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
});
administration.MapPut("/users/{username}/password", async (string username, ResetPasswordRequest request, UserAccountStore users, CancellationToken ct) =>
{
    try { await users.ResetPasswordAsync(username, request.Password, ct); return Results.NoContent(); }
    catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (KeyNotFoundException exception) { return Results.NotFound(new { error = exception.Message }); }
});

var api = app.MapGroup("/api/operator").RequireAuthorization(OperatorPolicies.View);
api.MapGet("/status", (OperatorBackendClient backend, CancellationToken ct) => backend.GetStatusAsync(ct));
api.MapGet("/measurements", (OperatorBackendClient backend, CancellationToken ct) => backend.GetMeasurementsAsync(ct));
api.MapGet("/measurements/{id:guid}", (Guid id, OperatorBackendClient backend, CancellationToken ct) =>
    backend.GetMeasurementAsync(id, ct));
api.MapGet("/measurements/{id:guid}/stages/{stage}", (Guid id, string stage, OperatorBackendClient backend, CancellationToken ct) =>
    backend.GetStageAsync(id, stage, ct));
api.MapGet("/audit", (IAuditStore audit, CancellationToken ct) => audit.ReadAsync(ct))
    .RequireAuthorization(OperatorPolicies.DangerousCommand);
api.MapGet("/engineering/system", (EngineeringTools tools) => Results.Ok(tools.SystemSnapshot()))
    .RequireAuthorization(OperatorPolicies.DangerousCommand);
api.MapGet("/engineering/logs", (string? service, string? level, int? take, EngineeringTools tools) =>
    Results.Ok(tools.ReadLogs(service, level, take ?? 100))).RequireAuthorization(OperatorPolicies.DangerousCommand);
api.MapGet("/engineering/config", async (EngineeringTools tools, CancellationToken ct) =>
    Results.Ok(new { source = "gateway-draft", applied = false, values = await tools.GetConfigurationAsync(ct) }))
    .RequireAuthorization(OperatorPolicies.DangerousCommand);
api.MapPut("/engineering/config", () => Results.Problem(statusCode: 409, title: "UseScopedConfigurationApi",
    detail: "Apply settings through the scoped engineer API; gateway drafts are not active service configuration."))
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
