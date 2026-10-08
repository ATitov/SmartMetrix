using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SmartMetrix.ServiceDefaults;

public sealed record RuntimeSettingsUpdate(string ExpectedRevision, Dictionary<string, string> Values);
public sealed record RuntimeSettingsSnapshot(string Revision, Dictionary<string, string> Values, bool RestartRequired = false);
public interface IRuntimeSettingsApplier<in T> { void Apply(T value); }
public sealed class RuntimeSettingsApplyException(string message, Exception inner) : Exception(message, inner);

public sealed class RuntimeSettings<T>(IConfiguration configuration, IOptionsMonitor<T> monitor,
    IEnumerable<IValidateOptions<T>> validators, IEnumerable<IPostConfigureOptions<T>> postConfigurations,
    string section, string path, string[] editable) where T : class, new()
{
    private readonly object gate = new();
    private string revision = configuration["RuntimeSettingsRevision"] ?? "initial";
    public Action<T>? ApplyToBackend { get; set; }

    public RuntimeSettingsSnapshot Read()
    {
        lock (gate)
        {
            // Serialize the bound live options, including defaults, rather than invented gateway defaults.
            var bound = new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(monitor.CurrentValue))).Build();
            return new(revision, editable.ToDictionary(key => key, key => bound[key] ?? "", StringComparer.OrdinalIgnoreCase));
        }
    }

    public RuntimeSettingsSnapshot Apply(RuntimeSettingsUpdate request)
    {
        lock (gate)
        {
            if (request.ExpectedRevision != revision) throw new InvalidOperationException("Configuration revision changed.");
            if (request.Values is null || request.Values.Count == 0 || request.Values.Any(x => !editable.Contains(x.Key, StringComparer.OrdinalIgnoreCase) || x.Value is null || x.Value.Length > 256))
                throw new ArgumentException("Only advertised runtime settings can be changed.");
            var current = Read();
            if (request.Values.All(x => current.Values[x.Key] == x.Value)) return current;
            var previousOptions = monitor.CurrentValue;
            var values = configuration.AsEnumerable().Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            var boundOptions = new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(previousOptions))).Build();
            foreach (var pair in boundOptions.AsEnumerable().Where(x => x.Value is not null)) values[section + ":" + pair.Key] = pair.Value;
            foreach (var pair in request.Values) values[section + ":" + pair.Key] = pair.Value;
            var nextRevision = Guid.NewGuid().ToString("N");
            if (section == "Quality")
            {
                values["Quality:Metrics:Version"] = "runtime-" + nextRevision;
                values["Quality:Metrics:FieldValidated"] = "false";
                foreach (var scene in configuration.GetSection("Quality:Scenes").GetChildren())
                    values[$"Quality:Scenes:{scene.Key}:Version"] = "runtime-" + nextRevision;
            }
            if (section == "BlockAnalysis") values["BlockAnalysis:AlgorithmVersion"] = "visible-surface-pca-v2-runtime-" + nextRevision;
            var candidate = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            T options;
            try { options = candidate.GetSection(section).Get<T>() ?? new T(); }
            catch (InvalidOperationException error) { throw new ArgumentException("Invalid setting format.", error); }
            foreach (var postConfigure in postConfigurations) postConfigure.PostConfigure(Options.DefaultName, options);
            foreach (var validator in validators)
            {
                var result = validator.Validate(Options.DefaultName, options);
                if (result.Failed) throw new ArgumentException(string.Join("; ", result.Failures));
            }
            DeploymentProfile.Validate(candidate);
            var persisted = editable.ToDictionary(key => section + ":" + key, key => candidate[section + ":" + key]);
            if (section == "Quality")
            {
                persisted["Quality:Metrics:Version"] = values["Quality:Metrics:Version"];
                persisted["Quality:Metrics:FieldValidated"] = "false";
                foreach (var scene in candidate.GetSection("Quality:Scenes").GetChildren())
                    persisted[$"Quality:Scenes:{scene.Key}:Version"] = "runtime-" + nextRevision;
            }
            if (section == "BlockAnalysis") persisted["BlockAnalysis:AlgorithmVersion"] = values["BlockAnalysis:AlgorithmVersion"];
            persisted["RuntimeSettingsRevision"] = nextRevision;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(persisted));
                File.Move(temporary, path, true);
                ((IConfigurationRoot)configuration).Reload();
                _ = monitor.CurrentValue;
                ApplyToBackend?.Invoke(monitor.CurrentValue);
                revision = nextRevision;
                return Read();
            }
            catch (Exception error)
            {
                try
                {
                    if (previous is null) File.Delete(path); else File.WriteAllBytes(path, previous);
                    ((IConfigurationRoot)configuration).Reload();
                    ApplyToBackend?.Invoke(previousOptions);
                }
                catch (Exception rollbackError)
                {
                    throw new RuntimeSettingsApplyException("Applying configuration failed; restoring the backend also failed. Check service readiness.", new AggregateException(error, rollbackError));
                }
                throw new RuntimeSettingsApplyException("Applying configuration failed; previous settings were restored. Check service readiness.", error);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

public static class RuntimeSettingsEndpoints
{
    public static void AddRuntimeSettings<T>(this WebApplicationBuilder builder, string section, params string[] editable) where T : class, new()
    {
        var path = Path.Combine(builder.Environment.ContentRootPath, "data", "runtime-settings.json");
        builder.Configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
        DeploymentProfile.Validate(builder.Configuration);
        builder.Services.AddSingleton(services => new RuntimeSettings<T>(builder.Configuration,
            services.GetRequiredService<IOptionsMonitor<T>>(), services.GetServices<IValidateOptions<T>>(), services.GetServices<IPostConfigureOptions<T>>(), section, path, editable)
        {
            ApplyToBackend = options => { foreach (var applier in services.GetServices<IRuntimeSettingsApplier<T>>()) applier.Apply(options); }
        });
    }

    public static void MapRuntimeSettings<T>(this WebApplication app) where T : class, new()
    {
        // Service endpoints are private deployment APIs; the gateway provides role/scope/CSRF checks.
        app.MapGet("/v1/configuration", (RuntimeSettings<T> settings) => Results.Ok(settings.Read()));
        app.MapPut("/v1/configuration", (RuntimeSettingsUpdate request, RuntimeSettings<T> settings) =>
        {
            try { return Results.Ok(settings.Apply(request)); }
            catch (RuntimeSettingsApplyException exception) { return Results.Problem(statusCode: 503, title: "ConfigurationApplyFailed", detail: exception.Message); }
            catch (InvalidOperationException exception) { return Results.Problem(statusCode: 409, title: "ConfigurationConflict", detail: exception.Message); }
            catch (ArgumentException exception) { return Results.Problem(statusCode: 422, title: "InvalidConfiguration", detail: exception.Message); }
        });
    }
}
