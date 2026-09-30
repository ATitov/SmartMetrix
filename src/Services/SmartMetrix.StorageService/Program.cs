using SmartMetrix.Persistence;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;
using SmartMetrix.ServiceDefaults;
using SmartMetrix.StorageService;

var builder = WebApplication.CreateBuilder(args);
builder.AddSmartMetrixServiceDefaults();
builder.AddSmartMetrixPersistence("storage");

builder.Services
    .AddOptions<StorageOptions>()
    .Bind(builder.Configuration.GetSection(StorageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IAmazonS3>(services =>
{
    var options = services.GetRequiredService<IOptions<StorageOptions>>().Value;
    return new AmazonS3Client(
        new BasicAWSCredentials(options.AccessKey, options.SecretKey),
        new AmazonS3Config
        {
            ServiceURL = options.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = options.Region
        });
});
builder.Services.AddSingleton<ObjectStorage>();
builder.Services.AddHostedService<StorageInitializer>();
builder.Services.AddExceptionHandler<StorageExceptionHandler>();
builder.Services.AddHealthChecks().AddCheck<StorageHealthCheck>("object-storage", tags: ["ready"]);

var app = builder.Build();
app.UseExceptionHandler(_ => { });
app.UseSmartMetrixServiceDefaults();
app.MapSmartMetrixDefaultEndpoints();

var artifacts = app.MapGroup("/v1/measurements/{measurementId:guid}/artifacts");

artifacts.MapPut("/{**artifactPath}", async (
    Guid measurementId,
    string artifactPath,
    HttpRequest request,
    ObjectStorage storage,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.ContentType))
    {
        return Results.Problem("Content-Type is required.", statusCode: StatusCodes.Status400BadRequest);
    }

    var result = await storage.UploadAsync(
        measurementId,
        artifactPath,
        request.Body,
        request.ContentType,
        request.Headers["X-Content-SHA256"].FirstOrDefault(),
        request.Headers["X-Provenance"].FirstOrDefault(),
        cancellationToken);

    return result.Created
        ? Results.Created(result.Metadata.Uri, result.Metadata)
        : Results.Ok(result.Metadata);
});

artifacts.MapGet("/metadata/{**artifactPath}", async (
    Guid measurementId,
    string artifactPath,
    ObjectStorage storage,
    CancellationToken cancellationToken) =>
    Results.Ok(await storage.GetMetadataAsync(measurementId, artifactPath, cancellationToken)));

artifacts.MapGet("/download/{**artifactPath}", async (
    Guid measurementId,
    string artifactPath,
    ObjectStorage storage,
    HttpResponse response,
    CancellationToken cancellationToken) =>
{
    var download = await storage.DownloadVerifiedAsync(measurementId, artifactPath, cancellationToken);
    response.ContentType = download.Metadata.ContentType;
    response.ContentLength = download.Metadata.Size;
    response.Headers.ETag = $"\"{download.Metadata.Sha256}\"";
    await using (download)
    {
        await download.Content.CopyToAsync(response.Body, cancellationToken);
    }
});

artifacts.MapGet("/uri/{**artifactPath}", async (
    Guid measurementId,
    string artifactPath,
    ObjectStorage storage,
    CancellationToken cancellationToken) =>
    Results.Ok(await storage.GetPresignedDownloadAsync(measurementId, artifactPath, cancellationToken)));

app.Run();

public partial class Program;
