using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace SmartMetrix.StorageService;

public sealed class StorageInitializer(IAmazonS3 s3, IOptions<StorageOptions> options, SmartMetrix.Persistence.PostgresDatabase? database = null) : IHostedService
{
    private readonly StorageOptions _options = options.Value;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, _options.Bucket))
        {
            await s3.PutBucketAsync(new PutBucketRequest { BucketName = _options.Bucket }, cancellationToken);
        }

        await s3.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
        {
            BucketName = _options.Bucket,
            Configuration = new LifecycleConfiguration
            {
                Rules =
                [
                    new LifecycleRule
                    {
                        Id = "artifact-retention",
                        Status = database is null ? LifecycleRuleStatus.Enabled : LifecycleRuleStatus.Disabled,
                        Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = "measurements/" } },
                        Expiration = new LifecycleRuleExpiration { Days = _options.RetentionDays }
                    },
                    new LifecycleRule
                    {
                        Id = "incomplete-upload-cleanup",
                        Status = LifecycleRuleStatus.Enabled,
                        Filter = new LifecycleFilter { LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = ".uploads/" } },
                        Expiration = new LifecycleRuleExpiration { Days = _options.IncompleteUploadRetentionDays },
                        AbortIncompleteMultipartUpload = new LifecycleRuleAbortIncompleteMultipartUpload { DaysAfterInitiation = _options.IncompleteUploadRetentionDays }
                    }
                ]
            }
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class StorageHealthCheck(IAmazonS3 s3, IOptions<StorageOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await AmazonS3Util.DoesS3BucketExistV2Async(s3, options.Value.Bucket)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Storage bucket does not exist.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Object storage is unavailable.", exception);
        }
    }
}

public sealed class StorageExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception switch
        {
            ArtifactConflictException => StatusCodes.Status409Conflict,
            ArtifactIntegrityException => StatusCodes.Status422UnprocessableEntity,
            ArgumentException => StatusCodes.Status400BadRequest,
            AmazonS3Exception { StatusCode: System.Net.HttpStatusCode.NotFound } => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status500InternalServerError
        };
        if (status == StatusCodes.Status500InternalServerError) return false;

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = ReasonPhrases.GetReasonPhrase(status),
            Detail = exception.Message
        }, cancellationToken);
        return true;
    }
}
