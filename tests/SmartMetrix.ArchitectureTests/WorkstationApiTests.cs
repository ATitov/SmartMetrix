using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartMetrix.ApiGateway;
using SmartMetrix.Domain.Excavation;
using SmartMetrix.ExcavatorReplay;

namespace SmartMetrix.ArchitectureTests;

public sealed class WorkstationApiTests : IAsyncLifetime, IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "smartmetrix-arm-" + Guid.NewGuid().ToString("N"));
    private readonly Guid measurementId = Guid.NewGuid();
    private readonly Guid foreignId = Guid.NewGuid();
    private WebApplication app = null!;
    private HttpClient client = null!;
    private int writes;
    private bool testMeasurement;
    private static readonly string[] Roles = ["operator", "geologist", "surveyor", "engineer", "administrator"];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        // Cookie/CSRF tests must not depend on the Windows user's persistent key ring.
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddWorkstationApi(builder.Configuration);
        builder.Services.Configure<ExcavationReportOptions>(options => options.Roots["north"] = Path.Combine(root, "replays"));
        builder.Services.Configure<WorkstationOptions>(options =>
        {
            options.Scopes.Add(new()
            {
                Id = "north",
                SiteId = "site",
                ExcavatorId = "rig",
                RigId = "rig",
                CoordinateSystemId = "quarry",
                Services = new() { ["orchestrator"] = "http://backend/", ["controlPoints"] = "http://backend/", ["calibration"] = "http://backend/", ["positioning"] = "http://backend/", ["sync"] = "http://backend/" }
            });
            foreach (var role in Roles) options.ApiKeyScopes["test-" + role] = ["north"];
            options.ReviewPath = Path.Combine(root, "reviews.json");
            options.BackupDirectory = Path.Combine(root, "backups");
        });
        builder.Services.Configure<OperatorApiOptions>(options =>
        {
            foreach (var role in Roles) options.ApiKeys["test-" + role] = role;
            options.ApiKeys["no-scope"] = "operator";
            options.UserStorePath = Path.Combine(root, "users.json");
            options.AuditPath = Path.Combine(root, "audit.jsonl");
            options.BootstrapAdminPassword = "test-password-long-enough";
        });
        builder.Services.AddAuthentication("test-combined")
            .AddPolicyScheme("test-combined", "test-combined", options => options.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey("X-API-Key") ? ApiKeyAuthenticationHandler.SchemeName : "cookie")
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { })
            .AddCookie("cookie", options =>
            {
                options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
                options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAuditStore, JsonAuditStore>();
        builder.Services.AddSingleton<UserAccountStore>();
        builder.Services.AddSingleton<EngineeringTools>();
        builder.Services.AddTransient(_ => new WorkstationBackend(new HttpClient(new BackendHandler(this))));
        app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWorkstationSecurity();
        app.MapWorkstationApi();
        // Only this test host issues a pre-authenticated cookie.
        app.MapGet("/test/session", async context => await context.SignInAsync("cookie", new ClaimsPrincipal(new ClaimsIdentity([
            new(ClaimTypes.Name, "cookie-user"), new(ClaimTypes.Role, "operator"), new(WorkstationIdentity.ScopeClaim, "north")], "cookie"))));
        await app.Services.GetRequiredService<UserAccountStore>().InitializeAsync(CancellationToken.None);
        await app.StartAsync();
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await app.DisposeAsync();
        Directory.Delete(root, true);
    }

    public void Dispose() => client?.Dispose();

    [Theory]
    [InlineData(null, 401)]
    [InlineData("invalid", 401)]
    [InlineData("no-scope", 403)]
    [InlineData("test-operator", 200)]
    [InlineData("test-geologist", 200)]
    [InlineData("test-surveyor", 200)]
    [InlineData("test-engineer", 200)]
    [InlineData("test-administrator", 200)]
    [Trait("Requirement", "ARM-SEC-01")]
    public async Task AuthenticationAndScopeAreEnforced(string? key, int status)
    {
        Key(key);
        using var response = await client.GetAsync("/api/v1/scopes/north/measurements");
        Assert.Equal(status, (int)response.StatusCode);
    }

    private static readonly System.Text.Json.JsonSerializerOptions ReplayJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    private ReplayRunResult CreateReplay(string excavatorId = "rig")
    {
        var origin = new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.FromHours(6));
        ExcavationPhase[] phases = [ExcavationPhase.Digging, ExcavationPhase.LoadedSwing, ExcavationPhase.Unloading,
            ExcavationPhase.Returning, ExcavationPhase.Waiting, ExcavationPhase.Waiting];
        var samples = phases.Select((phase, index) => new ExcavatorTelemetry(1, Guid.NewGuid(), excavatorId, "fixture", "clock",
            index * 1_000_000_000L, origin.AddSeconds(index), true, SignalQuality.Good, true,
            phase == ExcavationPhase.Digging, phase == ExcavationPhase.Unloading, phase == ExcavationPhase.LoadedSwing,
            phase is ExcavationPhase.LoadedSwing or ExcavationPhase.Returning ? 10 : 0, 0));
        var input = Path.Combine(root, "telemetry-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(input, samples.Select(x => System.Text.Json.JsonSerializer.Serialize(x, ReplayJson)));
        return ReplayRunner.Run(input, Path.Combine(root, "replays"), origin, origin.AddSeconds(10));
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("no-scope", 403)]
    [InlineData("test-operator", 200)]
    public async Task ExcavationReportsRequireAuthenticationAndScope(string? key, int expected)
    {
        CreateReplay();
        Key(key);
        using var response = await client.GetAsync("/api/v1/scopes/north/excavation/runs");
        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Fact]
    public async Task ExcavationReportsHideOtherMachinesAndExportValidatedCsv()
    {
        var own = CreateReplay();
        var foreign = CreateReplay("other-machine");
        Key("test-operator");
        var list = await client.GetStringAsync("/api/v1/scopes/north/excavation/runs");
        Assert.Contains(own.RunId, list, StringComparison.Ordinal);
        Assert.DoesNotContain(foreign.RunId, list, StringComparison.Ordinal);
        Assert.DoesNotContain(root, list, StringComparison.Ordinal);
        using var hidden = await client.GetAsync($"/api/v1/scopes/north/excavation/runs/{foreign.RunId}/report");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var csv = await client.GetAsync($"/api/v1/scopes/north/excavation/runs/{own.RunId}/csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", csv.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("true", await csv.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var detail = await client.GetStringAsync($"/api/v1/scopes/north/excavation/runs/{own.RunId}/report");
        Assert.Contains("\"isSynthetic\":true", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExcavationCorruptionIsExcludedAndCannotBeExported()
    {
        var run = CreateReplay();
        File.AppendAllText(Path.Combine(run.Directory, "shift-report.json"), "corrupt");
        Key("test-engineer");
        var list = JsonNode.Parse(await client.GetStringAsync("/api/v1/scopes/north/excavation/runs"))!;
        Assert.Equal("Degraded", list["state"]!.GetValue<string>());
        Assert.Equal(1, list["invalidCount"]!.GetValue<int>());
        Assert.Empty(list["items"]!.AsArray());
        using var report = await client.GetAsync($"/api/v1/scopes/north/excavation/runs/{run.RunId}/report");
        using var csv = await client.GetAsync($"/api/v1/scopes/north/excavation/runs/{run.RunId}/csv");
        Assert.Equal(HttpStatusCode.Conflict, report.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, csv.StatusCode);
    }

    [Fact]
    public async Task ExcavationMalformedManifestDoesNotBreakRunListing()
    {
        var run = CreateReplay();
        File.WriteAllText(Path.Combine(run.Directory, "manifest.json"), "{}");
        Key("test-operator");
        var list = JsonNode.Parse(await client.GetStringAsync("/api/v1/scopes/north/excavation/runs"))!;
        Assert.Equal(1, list["invalidCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task ExcavationInvalidIdsCannotAddressArbitraryFiles()
    {
        CreateReplay();
        Key("test-operator");
        using var response = await client.GetAsync("/api/v1/scopes/north/excavation/runs/manifest.json/report");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("operator", "geologist", 403)]
    [InlineData("engineer", "geologist", 403)]
    [InlineData("administrator", "geologist", 403)]
    [InlineData("geologist", "geologist", 200)]
    [InlineData("surveyor", "surveyor", 200)]
    [InlineData("administrator", "surveyor", 403)]
    [InlineData("engineer", "engineer", 200)]
    [InlineData("operator", "engineer", 403)]
    [InlineData("administrator", "administrator", 200)]
    [InlineData("engineer", "administrator", 403)]
    [Trait("Requirement", "ARM-SEC-02")]
    public async Task RolesDoNotInheritProfessionalPermissions(string role, string workstation, int status)
    {
        Key("test-" + role);
        var path = workstation switch
        {
            "geologist" => $"geologist/measurements/{measurementId}/reviews",
            "surveyor" => "surveyor/control-points",
            "engineer" => "engineer/calibrations",
            _ => "administrator/sync/status"
        };
        using var response = await client.GetAsync("/api/v1/scopes/north/" + path);
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "ARM-SEC-03")]
    public async Task ForeignObjectsAreHiddenAndLegacyApiCannotBypassScope()
    {
        Key("test-operator");
        using var foreign = await client.GetAsync($"/api/v1/scopes/north/measurements/{foreignId}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var stage = await client.GetAsync($"/api/v1/scopes/north/measurements/{foreignId}/stages/analysis");
        Assert.Equal(HttpStatusCode.NotFound, stage.StatusCode);
        var list = await client.GetStringAsync("/api/v1/scopes/north/measurements");
        Assert.DoesNotContain(foreignId.ToString(), list, StringComparison.Ordinal);
        Assert.DoesNotContain("internal-secret", list, StringComparison.Ordinal);
        using var legacy = await client.GetAsync("/api/operator/measurements");
        Assert.Equal(HttpStatusCode.Gone, legacy.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "ARM-GEO-01")]
    public async Task ReviewsPersistAreIdempotentAndRejectStaleOrReusedCommands()
    {
        Key("test-geologist");
        var request = new WorkstationDecision(Guid.NewGuid(), 3, "Approved", "Checked boundaries");
        var path = $"/api/v1/scopes/north/geologist/measurements/{measurementId}/reviews";
        using var first = await client.PostAsJsonAsync(path, request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var replay = await client.PostAsJsonAsync(path, request);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using var changed = await client.PostAsJsonAsync(path, request with { Decision = "NeedsRevision" });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var stale = await client.PostAsJsonAsync(path, request with { CommandId = Guid.NewGuid(), ExpectedVersion = 2 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var reopened = new WorkstationReviewStore(app.Services.GetRequiredService<IOptions<WorkstationOptions>>(), app.Environment);
        var rows = await reopened.ListAsync("north", measurementId, CancellationToken.None);
        Assert.Single(rows);
        Assert.StartsWith("api-key:", rows[0].Actor, StringComparison.Ordinal);
        Assert.DoesNotContain("test-geo", rows[0].Actor, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("operator", 201)]
    [InlineData("engineer", 201)]
    [InlineData("geologist", 403)]
    [InlineData("surveyor", 403)]
    [InlineData("administrator", 403)]
    [Trait("Requirement", "ARM-OP-01")]
    public async Task StartRequiresOperationalRole(string role, int status)
    {
        Key("test-" + role);
        using var response = await client.PostAsJsonAsync("/api/v1/scopes/north/measurements", new WorkstationStart(Guid.NewGuid(), Guid.NewGuid(), "Field measurement"));
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "ARM-OP-02")]
    public async Task InvalidCommandsNeverReachBackendAndUnknownReadinessIsNotReady()
    {
        Key("test-operator");
        using var response = await client.PostAsJsonAsync("/api/v1/scopes/north/measurements", new WorkstationStart(Guid.Empty, Guid.NewGuid(), " "));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, writes);
        var state = await client.GetFromJsonAsync<JsonObject>("/api/v1/scopes/north/operator/status");
        Assert.Equal("Degraded", state!["state"]!.GetValue<string>());
        Assert.Null(state["canStart"]);
        Assert.Contains("NotConfigured", state.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "ARM-SRV-01")]
    public async Task ControlPointsFilterCoordinateSystemAndRejectWrongScopeWrites()
    {
        Key("test-surveyor");
        var points = await client.GetStringAsync("/api/v1/scopes/north/surveyor/control-points");
        Assert.Contains("CP-1", points, StringComparison.Ordinal);
        Assert.DoesNotContain("CP-FOREIGN", points, StringComparison.Ordinal);
        using var response = await client.PostAsJsonAsync("/api/v1/scopes/north/surveyor/control-points", new { pointId = "CP-2", coordinateSystemId = "foreign" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, writes);
    }

    [Fact]
    [Trait("Requirement", "ARM-SEC-04")]
    public async Task CookieMutationsRequireAntiforgeryToken()
    {
        await client.GetStringAsync("/test/session");
        var request = new WorkstationStart(Guid.NewGuid(), Guid.NewGuid(), "Cookie command");
        using var denied = await client.PostAsJsonAsync("/api/v1/scopes/north/measurements", request);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal(0, writes);
        var csrf = await client.GetFromJsonAsync<JsonObject>("/api/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf!["token"]!.GetValue<string>());
        using var allowed = await client.PostAsJsonAsync("/api/v1/scopes/north/measurements", request);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "ARM-ADM-01")]
    public async Task MultipleRolesAndScopesAreStoredWithoutExposingPasswordMaterial()
    {
        Key("test-administrator");
        using var added = await client.PostAsJsonAsync("/api/v1/administrator/users/", new CreateUserRequest("mixed", "Mixed User", "operator", "long-password-123", ["surveyor"], ["north"]));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var store = app.Services.GetRequiredService<UserAccountStore>();
        var user = await store.FindAsync("mixed", CancellationToken.None);
        var principal = WorkstationIdentity.Principal(user!, "cookie");
        Assert.True(principal.IsInRole("operator"));
        Assert.True(principal.IsInRole("surveyor"));
        Assert.False(principal.IsInRole("administrator"));
        Assert.True(WorkstationIdentity.CanAccess(principal, "north"));
        var listing = await client.GetStringAsync("/api/v1/administrator/users/");
        Assert.DoesNotContain("long-password", listing, StringComparison.Ordinal);
        Assert.DoesNotContain(user!.PasswordHash, listing, StringComparison.Ordinal);
        using var badAccess = await client.PutAsJsonAsync("/api/v1/administrator/users/mixed/access", new UserAccessRequest("operator", [], ["other"]));
        Assert.Equal(HttpStatusCode.BadRequest, badAccess.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "ARM-ADM-02")]
    public async Task BackupVerificationChecksActualHashAndDetectsCorruption()
    {
        Key("test-administrator");
        var folder = Path.Combine(root, "backups");
        Directory.CreateDirectory(folder);
        var bytes = Encoding.UTF8.GetBytes("fixture backup bytes");
        await File.WriteAllBytesAsync(Path.Combine(folder, "B-01.zip"), bytes);
        await File.WriteAllTextAsync(Path.Combine(folder, "B-01.zip.sha256"), Convert.ToHexString(SHA256.HashData(bytes)));
        using var good = await client.PostAsync("/api/v1/administrator/backups/B-01/verify", null);
        Assert.True((await good.Content.ReadFromJsonAsync<JsonObject>())!["verified"]!.GetValue<bool>());
        await File.AppendAllTextAsync(Path.Combine(folder, "B-01.zip"), "corrupt");
        using var bad = await client.PostAsync("/api/v1/administrator/backups/B-01/verify", null);
        Assert.False((await bad.Content.ReadFromJsonAsync<JsonObject>())!["verified"]!.GetValue<bool>());
    }

    [Fact]
    [Trait("Requirement", "ARM-ENG-01")]
    public async Task RetryRequiresConfirmationAndOwnerAndDoesNotExposeInternalLocations()
    {
        Key("test-engineer");
        var command = new WorkstationMutation(Guid.NewGuid(), 3, "Repeat after fault", false);
        using var denied = await client.PostAsJsonAsync($"/api/v1/scopes/north/engineer/measurements/{measurementId}/retry", command);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        using var foreign = await client.PostAsJsonAsync($"/api/v1/scopes/north/engineer/measurements/{foreignId}/retry", command with { Confirmed = true });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(0, writes);
        var calibrations = await client.GetStringAsync("/api/v1/scopes/north/engineer/calibrations");
        Assert.DoesNotContain("internal-secret", calibrations, StringComparison.Ordinal);
    }

    private void Key(string? value)
    {
        client.DefaultRequestHeaders.Remove("X-API-Key");
        if (value is not null) client.DefaultRequestHeaders.Add("X-API-Key", value);
    }

    [Fact]
    public async Task SyntheticResultCannotReceiveProductionApproval()
    {
        testMeasurement = true;
        Key("test-geologist");
        var request = new WorkstationDecision(Guid.NewGuid(), 3, "Approved", "Checked boundaries");
        using var response = await client.PostAsJsonAsync($"/api/v1/scopes/north/geologist/measurements/{measurementId}/reviews", request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("TestResultCannotBeApproved", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(await app.Services.GetRequiredService<WorkstationReviewStore>().ListAsync("north", measurementId, CancellationToken.None));
    }

    private sealed class BackendHandler(WorkstationApiTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get) Interlocked.Increment(ref owner.writes);
            var path = request.RequestUri!.AbsolutePath;
            object measurement = new
            {
                id = owner.measurementId,
                excavatorId = "rig",
                coordinateSystemId = "quarry",
                version = 3,
                status = "Completed",
                d50 = 240,
                isTestData = owner.testMeasurement,
                pipeline = new { stages = new[] { new { name = "analysis", artifactUri = "http://internal-secret/data" } } }
            };
            object foreign = new { id = owner.foreignId, excavatorId = "other", coordinateSystemId = "quarry", version = 3, status = "Completed" };
            object result = path switch
            {
                "/measurements" when request.Method == HttpMethod.Get => new[] { measurement, foreign },
                "/control-points" => new[] { new { pointId = "CP-1", coordinateSystemId = "quarry" }, new { pointId = "CP-FOREIGN", coordinateSystemId = "other" } },
                "/api/calibrations" => new[] { new { id = Guid.Empty, payload = new { rigId = "rig", rectificationMapUri = "http://internal-secret/map" } } },
                "/v1/sync/status" => new { pendingItems = 0, pendingBytes = 0 },
                _ when path.Contains(owner.foreignId.ToString(), StringComparison.Ordinal) => foreign,
                _ => measurement
            };
            if (request.Method == HttpMethod.Post && path == "/measurements")
            {
                var payload = await request.Content!.ReadFromJsonAsync<JsonObject>(cancellationToken);
                Assert.Equal("rig", payload!["excavatorId"]!.GetValue<string>());
                Assert.Equal("quarry", payload["coordinateSystemId"]!.GetValue<string>());
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }
}
