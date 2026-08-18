using Microsoft.Extensions.Options;

namespace SmartMetrix.CloudSyncService;

public static class CloudSyncExtensions
{
    public static IServiceCollection AddCloudSync(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CloudSyncOptions>().Bind(configuration.GetSection(CloudSyncOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<SyncQueueStore>();
        services.AddHttpClient<CloudSyncUploader>((provider, client) =>
        {
            var value = provider.GetRequiredService<IOptions<CloudSyncOptions>>().Value;
            client.BaseAddress = new Uri(value.CloudBaseUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromMinutes(10);
        });
        services.AddHostedService<CloudSyncWorker>();
        return services;
    }

    public static IEndpointRouteBuilder MapCloudSyncEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v1/sync");
        group.MapPost("/queue", async (EnqueueSyncRequest request, SyncQueueStore queue, CancellationToken ct) =>
        {
            try
            {
                var item = await queue.EnqueueAsync(request, ct);
                return Results.Accepted($"/v1/sync/queue/{item.Id}", item);
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (FileNotFoundException exception) { return Results.BadRequest(new { error = exception.Message, path = exception.FileName }); }
        });
        group.MapGet("/status", async (SyncQueueStore queue, CancellationToken ct) => Results.Ok(await queue.GetStatusAsync(ct)));
        group.MapGet("/audit", async (int? take, SyncQueueStore queue, CancellationToken ct) => Results.Ok(await queue.GetAuditAsync(take ?? 100, ct)));
        return endpoints;
    }
}
