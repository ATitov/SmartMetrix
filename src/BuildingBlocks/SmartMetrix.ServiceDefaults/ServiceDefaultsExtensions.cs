using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace SmartMetrix.ServiceDefaults;

public static class ServiceDefaultsExtensions
{
    public static WebApplicationBuilder AddSmartMetrixServiceDefaults(this WebApplicationBuilder builder)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;
        builder.Configuration[$"{SmartMetrixServiceOptions.SectionName}:ServiceName"] ??=
            builder.Environment.ApplicationName;

        builder.Services
            .AddOptions<SmartMetrixServiceOptions>()
            .Bind(builder.Configuration.GetSection(SmartMetrixServiceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var shutdownTimeout = builder.Configuration.GetValue(
            $"{SmartMetrixServiceOptions.SectionName}:ShutdownTimeoutSeconds",
            30);
        builder.Services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(shutdownTimeout));

        builder.Services.AddProblemDetails();
        builder.Services
            .AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        builder.Services.ConfigureHttpClientDefaults(httpClientBuilder =>
            httpClientBuilder.ConfigureHttpClient((services, client) =>
            {
                var options = services.GetRequiredService<IOptions<SmartMetrixServiceOptions>>().Value;
                client.Timeout = options.HttpClientTimeout;
            }));

        AddFileLogging(builder);

        AddOpenTelemetry(builder);
        return builder;
    }

    public static WebApplication UseSmartMetrixServiceDefaults(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseMiddleware<CorrelationContextMiddleware>();
        return app;
    }

    public static WebApplication MapSmartMetrixDefaultEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => Results.Redirect("/info"));
        app.MapGet("/info", (
            IOptions<SmartMetrixServiceOptions> options,
            IWebHostEnvironment environment) => Results.Ok(new
            {
                service = options.Value.ServiceName,
                version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
                environment = environment.EnvironmentName,
                utcNow = DateTimeOffset.UtcNow
            }));

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live")
        });
        app.MapHealthChecks("/ready", new HealthCheckOptions
        {
            Predicate = _ => true
        });
        return app;
    }

    private static void AddFileLogging(WebApplicationBuilder builder)
    {
        var directory = builder.Configuration["SmartMetrix:FileLogging:Directory"];
        if (string.IsNullOrWhiteSpace(directory)) return;

        var serviceName = builder.Configuration[$"{SmartMetrixServiceOptions.SectionName}:ServiceName"]!;
        builder.Logging.AddProvider(new JsonFileLoggerProvider(directory, serviceName));
    }

    private static void AddOpenTelemetry(WebApplicationBuilder builder)
    {
        var serviceName = builder.Configuration[$"{SmartMetrixServiceOptions.SectionName}:ServiceName"]!;
        var hasOtlpEndpoint = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            if (hasOtlpEndpoint)
            {
                logging.AddOtlpExporter();
            }
        });

        var telemetry = builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing => tracing
                .AddSource(SmartMetrixTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());

        if (hasOtlpEndpoint)
        {
            telemetry.WithMetrics(metrics => metrics.AddOtlpExporter());
            telemetry.WithTracing(tracing => tracing.AddOtlpExporter());
        }
    }
}
