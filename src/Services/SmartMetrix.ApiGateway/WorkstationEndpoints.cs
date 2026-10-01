using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace SmartMetrix.ApiGateway;

public static class WorkstationEndpoints
{
    private static readonly string[] Stages = ["calibration", "capture", "pose", "quality", "depth", "segmentation", "analysis", "georeference", "result"];
    private static readonly string[] ReadinessServices = ["orchestrator", "trigger", "camera", "quality", "depth", "segmentation", "storage", "positioning", "calibration", "analysis", "georeference"];
    private static readonly string[] RuntimeServices = ["camera", "depth", "segmentation", "quality", "analysis"];

    public static IServiceCollection AddWorkstationApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WorkstationOptions>().Bind(configuration.GetSection(WorkstationOptions.Section))
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 120 &&
                options.Scopes.Select(x => x.Id).Distinct().Count() == options.Scopes.Count &&
                options.Scopes.All(x => new[] { x.Id, x.SiteId, x.ExcavatorId, x.RigId, x.CoordinateSystemId }.All(WorkstationIdentity.Identifier) &&
                    x.Services.Values.All(v => Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo))),
                "Invalid workstation scope configuration.").ValidateOnStart();
        services.AddHttpClient<WorkstationBackend>((provider, client) =>
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<WorkstationOptions>>().Value.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<WorkstationReviewStore>();
        services.AddSingleton<WorkstationBackups>();
        services.AddAntiforgery(options => options.HeaderName = "X-CSRF-Token");
        return services;
    }

    public static void UseWorkstationSecurity(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1"))
            {
                context.Response.Headers.CacheControl = "no-store";
                var size = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if (size is { IsReadOnly: false }) size.MaxRequestBodySize = 1024 * 1024;
            }
            if (context.Request.Path.StartsWithSegments("/api") &&
                context.User.Identity?.IsAuthenticated == true &&
                context.User.Identity.AuthenticationType != ApiKeyAuthenticationHandler.SchemeName &&
                context.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException)
                {
                    await Results.Problem(statusCode: 400, title: "InvalidCsrfToken").ExecuteAsync(context);
                    return;
                }
            }
            // Legacy unscoped measurements must not bypass object authorization after scope migration.
            if (context.Request.Path.StartsWithSegments("/api/operator") &&
                context.RequestServices.GetRequiredService<IOptions<WorkstationOptions>>().Value.Scopes.Count > 0)
            {
                await Results.Problem(statusCode: 410, title: "UseScopedWorkstationApi").ExecuteAsync(context);
                return;
            }
            await next(context);
        });
    }

    public static void MapWorkstationApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken })).RequireAuthorization();
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.AddEndpointFilter(async (invocation, next) =>
        {
            try { return await next(invocation); }
            catch (WorkstationApiException error)
            {
                return Results.Problem(statusCode: error.Status, title: error.Code,
                    extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["traceId"] = invocation.HttpContext.TraceIdentifier });
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "InvalidRequest"); }
            catch (KeyNotFoundException) { return Results.Problem(statusCode: 404, title: "NotFound"); }
            catch (System.Text.Json.JsonException) { return Results.Problem(statusCode: 400, title: "InvalidJson"); }
        });
        api.MapGet("/workstations", (ClaimsPrincipal user, IOptions<WorkstationOptions> options) => Results.Ok(new
        {
            scoped = options.Value.Scopes.Count > 0,
            roles = user.FindAll(ClaimTypes.Role).Select(x => x.Value).Where(OperatorRoles.All.Contains).Distinct(),
            scopes = options.Value.Scopes.Where(x => WorkstationIdentity.CanAccess(user, x.Id))
                .Select(x => new { x.Id, x.SiteId, x.ExcavatorId, x.RigId, x.CoordinateSystemId })
        }));

        var scope = api.MapGroup("/scopes/{scopeId}");
        scope.RequireAuthorization(policy => policy.RequireRole(OperatorRoles.All));
        scope.AddEndpointFilter(async (invocation, next) =>
        {
            var id = invocation.HttpContext.Request.RouteValues["scopeId"]?.ToString() ?? "";
            var options = invocation.HttpContext.RequestServices.GetRequiredService<IOptions<WorkstationOptions>>().Value;
            if (!WorkstationIdentity.CanAccess(invocation.HttpContext.User, id)) throw new WorkstationApiException(403, "ScopeDenied");
            invocation.HttpContext.Items["workstation.scope"] = options.Scopes.FirstOrDefault(x => x.Id == id)
                ?? throw new WorkstationApiException(404, "ScopeNotFound");
            return await next(invocation);
        });

        scope.MapGet("/operator/status", async (HttpContext context, WorkstationBackend backend, CancellationToken ct) =>
        {
            var area = Area(context);
            var checks = await Task.WhenAll(ReadinessServices.Select(async name =>
            {
                var state = "Ready";
                try { await backend.GetAsync(area, name, "ready", ct); }
                catch (WorkstationApiException error) { state = error.Code == "NotConfigured" ? "NotConfigured" : "Unavailable"; }
                return new { name, state, checkedAt = DateTimeOffset.UtcNow };
            }));
            return Results.Ok(new
            {
                state = checks.All(x => x.state == "Ready") ? "Ready" : "Degraded",
                checks,
                canStart = (bool?)null,
                triggerConditionsChecked = false
            });
        });

        scope.MapGet("/measurements", async (int? limit, string? status, HttpContext context, WorkstationBackend backend, CancellationToken ct) =>
        {
            if (limit is < 1 or > 200) throw new WorkstationApiException(400, "InvalidLimit");
            var area = Area(context);
            var values = await backend.GetAsync(area, "orchestrator", "measurements?limit=200", ct) as JsonArray
                ?? throw new WorkstationApiException(502, "InvalidDependencyResponse");
            return Results.Ok(new
            {
                items = values.Where(x => x?["excavatorId"]?.GetValue<string>() == area.ExcavatorId &&
                x?["coordinateSystemId"]?.GetValue<string>() == area.CoordinateSystemId &&
                (status is null || x?["status"]?.GetValue<string>() == status)).Take(limit ?? 50).Select(x => WorkstationBackend.PublicMeasurement(x!)),
                windowSize = 200,
                checkedAt = DateTimeOffset.UtcNow
            });
        });
        scope.MapGet("/measurements/{id:guid}", async (Guid id, HttpContext context, WorkstationBackend backend, WorkstationReviewStore reviews, CancellationToken ct) =>
        {
            var area = Area(context);
            var value = await backend.MeasurementAsync(area, id, ct);
            var version = value["version"]!.GetValue<long>();
            return Results.Ok(new
            {
                measurement = WorkstationBackend.PublicMeasurement(value),
                review = (await reviews.ListAsync(area.Id, id, ct)).LastOrDefault(x => x.ResultVersion == version),
                synchronization = "Unknown"
            });
        });
        scope.MapGet("/measurements/{id:guid}/stages/{stage}", async (Guid id, string stage, HttpContext context, WorkstationBackend backend, CancellationToken ct) =>
        {
            if (!Stages.Contains(stage)) throw new WorkstationApiException(400, "InvalidStage");
            await backend.MeasurementAsync(Area(context), id, ct);
            return Results.Ok(WorkstationBackend.WithoutLocations(await backend.GetAsync(Area(context), "orchestrator", $"measurements/{id}/stages/{stage}", ct)));
        });
        scope.MapPost("/measurements", async (WorkstationStart request, HttpContext context, WorkstationBackend backend, IAuditStore audit, CancellationToken ct) =>
        {
            WorkstationValidation.Command(request.CommandId, 0, request.Reason);
            if (request.MeasurementId == Guid.Empty) throw new WorkstationApiException(400, "MeasurementIdRequired");
            var area = Area(context);
            var result = await backend.SendAsync(area, "orchestrator", HttpMethod.Post, "measurements",
                new { request.CommandId, request.MeasurementId, area.ExcavatorId, area.CoordinateSystemId, request.Reason }, Actor(context), ct);
            await Log(audit, context, "measurement.start", request.MeasurementId, request.Reason, ct);
            return Results.Created($"/api/v1/scopes/{area.Id}/measurements/{request.MeasurementId}", WorkstationBackend.PublicMeasurement(result));
        }).RequireAuthorization(policy => policy.RequireRole(OperatorRoles.Operator, OperatorRoles.Engineer));

        var geology = scope.MapGroup("/geologist").RequireAuthorization(policy => policy.RequireRole(OperatorRoles.Geologist));
        geology.MapGet("/measurements/{id:guid}/reviews", async (Guid id, HttpContext context, WorkstationBackend backend, WorkstationReviewStore reviews, CancellationToken ct) =>
        {
            await backend.MeasurementAsync(Area(context), id, ct);
            return Results.Ok(await reviews.ListAsync(Area(context).Id, id, ct));
        });
        geology.MapPost("/measurements/{id:guid}/reviews", async (Guid id, WorkstationDecision request, HttpContext context, WorkstationBackend backend, WorkstationReviewStore reviews, CancellationToken ct) =>
        {
            var area = Area(context);
            // Check ownership even for an idempotent replay.
            await backend.MeasurementAsync(area, id, ct);
            var review = await reviews.SaveAsync(new(area.Id, id, request.CommandId, request.ExpectedVersion, request.Decision,
                request.Reason, Actor(context), DateTimeOffset.UtcNow), async () =>
                {
                    var value = await backend.MeasurementAsync(area, id, ct);
                    return (value["version"]!.GetValue<long>(), value["status"]!.GetValue<string>());
                }, ct);
            // The persisted review itself is the immutable audit record for this command.
            return Results.Ok(review);
        });
        geology.MapGet("/measurements/{id:guid}/report", async (Guid id, HttpContext context, WorkstationBackend backend, WorkstationReviewStore reviews, CancellationToken ct) =>
        {
            var value = await backend.MeasurementAsync(Area(context), id, ct);
            return Results.Ok(new
            {
                schemaVersion = 1,
                generatedAt = DateTimeOffset.UtcNow,
                measurement = WorkstationBackend.PublicMeasurement(value),
                reviews = (await reviews.ListAsync(Area(context).Id, id, ct)).Where(x => x.ResultVersion == value["version"]!.GetValue<long>())
            });
        });

        var survey = scope.MapGroup("/surveyor").RequireAuthorization(policy => policy.RequireRole(OperatorRoles.Surveyor));
        survey.MapGet("/control-points", async (HttpContext context, WorkstationBackend backend, CancellationToken ct) =>
            Results.Ok(await Points(context, backend, ct)));
        survey.MapPost("/control-points", async (JsonObject request, HttpContext context, WorkstationBackend backend, IAuditStore audit, CancellationToken ct) =>
        {
            if (request["pointId"] is not JsonValue pointValue || !pointValue.TryGetValue<string>(out var pointId) ||
                !WorkstationIdentity.Identifier(pointId)) throw new WorkstationApiException(400, "InvalidPointId");
            RequireValue(request, "coordinateSystemId", Area(context).CoordinateSystemId);
            var result = await backend.SendAsync(Area(context), "controlPoints", HttpMethod.Post, "control-points", request, Actor(context), ct);
            await Log(audit, context, "controlPoint.create", null, request["pointId"]!.GetValue<string>(), ct);
            return Results.Ok(result);
        });
        survey.MapGet("/control-points/{pointId}/history", async (string pointId, HttpContext context, WorkstationBackend backend, CancellationToken ct) =>
        {
            if (!WorkstationIdentity.Identifier(pointId)) throw new WorkstationApiException(400, "InvalidPointId");
            var point = await backend.GetAsync(Area(context), "controlPoints", $"control-points/{pointId}", ct);
            if (point["coordinateSystemId"]?.GetValue<string>() != Area(context).CoordinateSystemId) throw new WorkstationApiException(404, "PointNotFound");
            var history = await backend.GetAsync(Area(context), "controlPoints", $"control-points/{pointId}/history", ct) as JsonArray
                ?? throw new WorkstationApiException(502, "InvalidDependencyResponse");
            return Results.Ok(history.Where(x => x?["coordinateSystemId"]?.GetValue<string>() == Area(context).CoordinateSystemId).Select(x => x!.DeepClone()));
        });
        survey.MapGet("/transforms", async (HttpContext context, WorkstationBackend backend, CancellationToken ct) =>
        {
            var transforms = await backend.GetAsync(Area(context), "positioning", $"api/transforms/{Area(context).ExcavatorId}", ct) as JsonArray
                ?? throw new WorkstationApiException(502, "InvalidDependencyResponse");
            return Results.Ok(transforms.Where(x => x?["coordinateSystemId"]?.GetValue<string>() == Area(context).CoordinateSystemId).Select(x => x!.DeepClone()));
        });
        survey.MapPost("/transforms", async (JsonObject request, HttpContext context, WorkstationBackend backend, IAuditStore audit, CancellationToken ct) =>
        {
            RequireValue(request, "excavatorId", Area(context).ExcavatorId);
            RequireValue(request, "coordinateSystemId", Area(context).CoordinateSystemId);
            var value = await backend.SendAsync(Area(context), "positioning", HttpMethod.Post, "api/transforms", request, Actor(context), ct);
            await Log(audit, context, "transform.create", null, "New transform version", ct);
            return Results.Ok(value);
        });

        var engineering = scope.MapGroup("/engineer").RequireAuthorization(policy => policy.RequireRole(OperatorRoles.Engineer));
        engineering.MapGet("/audit", async (HttpContext context, IAuditStore audit, CancellationToken ct) =>
            Results.Ok((await audit.ReadAsync(ct)).Where(x => x.ScopeId == Area(context).Id)));
        engineering.MapGet("/config", async (HttpContext context, WorkstationBackend backend, CancellationToken ct) =>
        {
            var snapshots = new JsonObject();
            foreach (var name in RuntimeServices)
            {
                try { snapshots[name] = await backend.GetAsync(Area(context), name, "v1/configuration", ct); }
                catch (WorkstationApiException error) { snapshots[name] = new JsonObject { ["error"] = error.Code }; }
            }
            return Results.Ok(snapshots);
        });
        engineering.MapPut("/config/{service}", async (string service, SmartMetrix.ServiceDefaults.RuntimeSettingsUpdate request,
            HttpContext context, WorkstationBackend backend, IAuditStore audit, CancellationToken ct) =>
        {
            if (!RuntimeServices.Contains(service)) throw new WorkstationApiException(400, "UnsupportedRuntimeSettings");
            var result = await backend.SendAsync(Area(context), service, HttpMethod.Put, "v1/configuration", request, Actor(context), ct);
            await Log(audit, context, "configuration.apply", null, service, ct);
            return Results.Ok(result);
        });
        engineering.MapPost("/measurements/{id:guid}/{command}", async (Guid id, string command, WorkstationMutation request,
            HttpContext context, WorkstationBackend backend, IAuditStore audit, CancellationToken ct) =>
        {
            if (command is not ("cancel" or "retry")) throw new WorkstationApiException(404, "UnknownCommand");
            WorkstationValidation.Command(request.CommandId, request.ExpectedVersion, request.Reason);
            if (!request.Confirmed) throw new WorkstationApiException(409, "ConfirmationRequired");
            await backend.MeasurementAsync(Area(context), id, ct);
            var result = await backend.SendAsync(Area(context), "orchestrator", HttpMethod.Post, $"measurements/{id}/{command}",
                new { request.CommandId, request.ExpectedVersion, request.Reason }, Actor(context), ct);
            await Log(audit, context, $"measurement.{command}", id, request.Reason, ct);
            return Results.Ok(WorkstationBackend.PublicMeasurement(result));
        });
        engineering.MapGet("/calibrations", async (HttpContext context, WorkstationBackend backend, CancellationToken ct) =>
            Results.Ok(WorkstationBackend.WithoutLocations(await backend.GetAsync(Area(context), "calibration", $"api/calibrations?rigId={Area(context).RigId}", ct))));
        engineering.MapPost("/calibrations", async (JsonObject request, HttpContext context, WorkstationBackend backend, IAuditStore audit, CancellationToken ct) =>
        {
            RequireValue(request, "rigId", Area(context).RigId);
            var value = await backend.SendAsync(Area(context), "calibration", HttpMethod.Post, "api/calibrations", request, Actor(context), ct);
            await Log(audit, context, "calibration.create", null, "Draft calibration", ct);
            return Results.Ok(WorkstationBackend.WithoutLocations(value));
        });
        engineering.MapGet("/segmentation/capabilities", async (HttpContext context, WorkstationBackend backend, CancellationToken ct) =>
            Results.Ok(WorkstationBackend.WithoutLocations(await backend.GetAsync(Area(context), "segmentation", "v1/segmentation/capabilities", ct))));

        api.MapGet("/engineer/system", (EngineeringTools tools) => Results.Ok(tools.SystemSnapshot()))
            .RequireAuthorization(policy => policy.RequireRole(OperatorRoles.Engineer));
        api.MapGet("/engineer/logs", (string? service, string? level, int? take, EngineeringTools tools) =>
            Results.Ok(tools.ReadLogs(service, level, take ?? 100)))
            .RequireAuthorization(policy => policy.RequireRole(OperatorRoles.Engineer));

        var backups = api.MapGroup("/administrator/backups").RequireAuthorization(policy => policy.RequireRole(OperatorRoles.Administrator));
        backups.MapGet("/", (WorkstationBackups store) => Results.Ok(store.List()));
        backups.MapPost("/{id}/verify", async (string id, WorkstationBackups store, HttpContext context, IAuditStore audit, CancellationToken ct) =>
        {
            var result = await store.VerifyAsync(id, ct);
            await Log(audit, context, "backup.verify", null, id, ct);
            return Results.Ok(result);
        });

        var administration = scope.MapGroup("/administrator").RequireAuthorization(policy => policy.RequireRole(OperatorRoles.Administrator));
        administration.MapGet("/sync/status", async (HttpContext context, WorkstationBackend backend, CancellationToken ct) =>
            Results.Ok(await backend.GetAsync(Area(context), "sync", "v1/sync/status", ct)));
        // Queue is a deployment-level resource; a scope must point to its dedicated queue service.
        administration.MapGet("/audit", async (HttpContext context, IAuditStore audit, CancellationToken ct) =>
            Results.Ok((await audit.ReadAsync(ct)).Where(x => x.ScopeId == Area(context).Id)));

        var users = api.MapGroup("/administrator/users").RequireAuthorization(policy => policy.RequireRole(OperatorRoles.Administrator));
        users.MapGet("/", (UserAccountStore store, CancellationToken ct) => store.ListAsync(ct));
        users.MapPost("/", async (CreateUserRequest request, UserAccountStore store, IOptions<WorkstationOptions> options, HttpContext context, IAuditStore audit, CancellationToken ct) =>
        {
            ValidateScopes(request.ScopeIds ?? [], options.Value);
            try { await store.AddAsync(request, ct); }
            catch (InvalidOperationException) { throw new WorkstationApiException(409, "UserExists"); }
            await Log(audit, context, "user.create", null, request.Username, ct);
            return Results.Created($"/api/v1/administrator/users/{Uri.EscapeDataString(request.Username)}", new { request.Username });
        });
        users.MapPut("/{username}/access", async (string username, UserAccessRequest request, UserAccountStore store, IOptions<WorkstationOptions> options, HttpContext context, IAuditStore audit, CancellationToken ct) =>
        {
            ValidateScopes(request.ScopeIds, options.Value);
            await store.SetAccessAsync(username, request, ct);
            await Log(audit, context, "user.access", null, username, ct);
            return Results.NoContent();
        });
        users.MapPut("/{username}/enabled/{enabled:bool}", async (string username, bool enabled, UserAccountStore store, HttpContext context, IAuditStore audit, CancellationToken ct) =>
        {
            if (username.Equals(Actor(context), StringComparison.OrdinalIgnoreCase) && !enabled) throw new WorkstationApiException(409, "CannotDisableSelf");
            await store.SetEnabledAsync(username, enabled, ct);
            await Log(audit, context, "user.enabled", null, username, ct);
            return Results.NoContent();
        });
        users.MapPut("/{username}/password", async (string username, ResetPasswordRequest request, UserAccountStore store, HttpContext context, IAuditStore audit, CancellationToken ct) =>
        {
            await store.ResetPasswordAsync(username, request.Password, ct);
            await Log(audit, context, "user.password", null, username, ct);
            return Results.NoContent();
        });
    }

    private static WorkstationScope Area(HttpContext context) => (WorkstationScope)context.Items["workstation.scope"]!;
    private static string Actor(HttpContext context) => context.User.Identity?.Name ?? "unknown";
    private static void RequireValue(JsonObject request, string field, string expected)
    {
        if (request[field] is not JsonValue value || !value.TryGetValue<string>(out var actual) || actual != expected)
            throw new WorkstationApiException(400, "ScopeMismatch");
    }
    private static void ValidateScopes(string[] scopes, WorkstationOptions options)
    {
        if (scopes is null || scopes.Any(id => !options.Scopes.Any(x => x.Id == id))) throw new WorkstationApiException(400, "InvalidScopes");
    }
    private static Task Log(IAuditStore audit, HttpContext context, string action, Guid? id, string reason, CancellationToken ct) =>
        audit.AppendAsync(AuditEntry.Create(context.User, action, id, reason, true) with
        { ScopeId = (context.Items["workstation.scope"] as WorkstationScope)?.Id, CorrelationId = context.TraceIdentifier }, ct);
    private static async Task<JsonArray> Points(HttpContext context, WorkstationBackend backend, CancellationToken ct)
    {
        var area = Area(context);
        var values = await backend.GetAsync(area, "controlPoints", "control-points", ct) as JsonArray
            ?? throw new WorkstationApiException(502, "InvalidDependencyResponse");
        return new JsonArray(values.Where(x => x?["coordinateSystemId"]?.GetValue<string>() == area.CoordinateSystemId).Select(x => x!.DeepClone()).ToArray());
    }
}
