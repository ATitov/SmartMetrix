using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NATS.Client.JetStream;
using NATS.Net;

namespace SmartMetrix.ArchitectureTests;

[CollectionDefinition("NatsInfrastructure", DisableParallelization = true)]
public sealed class NatsInfrastructureTestGroup;

internal sealed class NatsTestServer(string url, IContainer? container) : IAsyncDisposable
{
    public string Url { get; } = url;
    public string StreamName { get; } = "REVIEW_TEST_" + Guid.NewGuid().ToString("N");
    public bool StreamCreated { get; set; }

    public static async Task<NatsTestServer> CreateAsync()
    {
        var url = Environment.GetEnvironmentVariable("SMARTMETRIX_TEST_NATS");
        if (!string.IsNullOrWhiteSpace(url)) return new(url, null);
        var container = new ContainerBuilder("nats:2.11-alpine").WithCommand("--jetstream", "--store_dir=/data")
            .WithPortBinding(4222, true).WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(4222)).Build();
        try
        {
            await container.StartAsync();
            return new($"nats://{container.Hostname}:{container.GetMappedPublicPort(4222)}", container);
        }
        catch { await container.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (StreamCreated)
            {
                await using var client = new NatsClient(Url);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await client.CreateJetStreamContext().DeleteStreamAsync(StreamName, timeout.Token);
            }
        }
        finally { if (container is not null) await container.DisposeAsync(); }
    }
}

internal sealed class MinioTestServer(string endpoint, string accessKey, string secretKey, IContainer? container) : IAsyncDisposable
{
    public string Endpoint { get; } = endpoint;
    public string AccessKey { get; } = accessKey;
    public string SecretKey { get; } = secretKey;
    public string Bucket { get; } = "smartmetrix-integration-" + Guid.NewGuid().ToString("N");
    public bool BucketCreated { get; set; }

    public static async Task<MinioTestServer> CreateAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("SMARTMETRIX_TEST_MINIO");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var access = Environment.GetEnvironmentVariable("SMARTMETRIX_TEST_MINIO_ACCESS_KEY");
            var secret = Environment.GetEnvironmentVariable("SMARTMETRIX_TEST_MINIO_SECRET_KEY");
            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("External MinIO requires test credentials.");
            return new(endpoint, access, secret, null);
        }
        var container = new ContainerBuilder("minio/minio:RELEASE.2025-07-23T15-54-02Z")
            .WithEnvironment("MINIO_ROOT_USER", "smartmetrix").WithEnvironment("MINIO_ROOT_PASSWORD", "smartmetrix-integration-secret")
            .WithCommand("server", "/data").WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(9000)).Build();
        try
        {
            await container.StartAsync();
            return new($"http://{container.Hostname}:{container.GetMappedPublicPort(9000)}", "smartmetrix", "smartmetrix-integration-secret", container);
        }
        catch { await container.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (BucketCreated)
            {
                using var client = new AmazonS3Client(new BasicAWSCredentials(AccessKey, SecretKey), new AmazonS3Config { ServiceURL = Endpoint, ForcePathStyle = true });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                string? continuation = null;
                do
                {
                    var page = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = Bucket, ContinuationToken = continuation }, timeout.Token);
                    if (page.S3Objects is { Count: > 0 })
                        await client.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = Bucket, Objects = page.S3Objects.Select(x => new KeyVersion { Key = x.Key }).ToList() }, timeout.Token);
                    continuation = page.NextContinuationToken;
                } while (!string.IsNullOrEmpty(continuation));
                await client.DeleteBucketAsync(Bucket, timeout.Token);
            }
        }
        finally { if (container is not null) await container.DisposeAsync(); }
    }
}
