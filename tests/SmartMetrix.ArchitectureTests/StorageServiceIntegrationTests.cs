using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Options;
using SmartMetrix.StorageService;

namespace SmartMetrix.ArchitectureTests;

public sealed class StorageServiceIntegrationTests
{
    [InfrastructureFact]
    [Trait("Category", "Integration")]
    [Trait("Requirement", "STO-01")]
    public async Task MinioUploadIsIdempotentAndCorruptionIsDetected()
    {
        const string accessKey = "smartmetrix";
        const string secretKey = "smartmetrix-integration-secret";
        await using var container = new ContainerBuilder("minio/minio:latest")
            .WithEnvironment("MINIO_ROOT_USER", accessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", secretKey)
            .WithCommand("server", "/data")
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(9000))
            .Build();
        await container.StartAsync(CancellationToken.None);

        var storageOptions = new StorageOptions
        {
            Endpoint = $"http://{container.Hostname}:{container.GetMappedPublicPort(9000)}",
            AccessKey = accessKey,
            SecretKey = secretKey,
            Bucket = "smartmetrix-integration",
            RetentionDays = 1,
            IncompleteUploadRetentionDays = 1
        };
        using var s3 = new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config { ServiceURL = storageOptions.Endpoint, ForcePathStyle = true });
        var configuredOptions = Options.Create(storageOptions);
        var initializer = new StorageInitializer(s3, configuredOptions);
        await initializer.StartAsync(CancellationToken.None);
        var storage = new ObjectStorage(s3, configuredOptions);
        var measurementId = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes("frame-data");

        var first = await storage.UploadAsync(
            measurementId,
            "frames/camera-a.raw",
            new MemoryStream(payload),
            "application/octet-stream",
            null,
            "camera-service:test",
            CancellationToken.None);
        var repeated = await storage.UploadAsync(
            measurementId,
            "frames/camera-a.raw",
            new MemoryStream(payload),
            "application/octet-stream",
            first.Metadata.Sha256,
            "camera-service:test",
            CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(repeated.Created);
        Assert.Equal(first.Metadata, repeated.Metadata);
        Assert.StartsWith("s3://smartmetrix-integration/measurements/", first.Metadata.Uri, StringComparison.Ordinal);

        await using (var download = await storage.DownloadVerifiedAsync(measurementId, "frames/camera-a.raw", CancellationToken.None))
        using (var reader = new StreamReader(download.Content))
        {
            Assert.Equal("frame-data", await reader.ReadToEndAsync(CancellationToken.None));
        }

        var presigned = await storage.GetPresignedDownloadAsync(measurementId, "frames/camera-a.raw", CancellationToken.None);
        Assert.Contains("X-Amz-Signature", presigned.DownloadUri, StringComparison.OrdinalIgnoreCase);

        var corrupt = new PutObjectRequest
        {
            BucketName = storageOptions.Bucket,
            Key = $"measurements/{measurementId:D}/frames/camera-a.raw",
            InputStream = new MemoryStream(Encoding.UTF8.GetBytes("corrupt")),
            ContentType = first.Metadata.ContentType
        };
        corrupt.Metadata["sha256"] = first.Metadata.Sha256;
        corrupt.Metadata["measurement-id"] = measurementId.ToString("D");
        corrupt.Metadata["artifact-path"] = first.Metadata.ArtifactPath;
        corrupt.Metadata["stored-at"] = first.Metadata.StoredAt.ToString("O");
        await s3.PutObjectAsync(corrupt, CancellationToken.None);

        await Assert.ThrowsAsync<ArtifactIntegrityException>(() =>
            storage.DownloadVerifiedAsync(measurementId, "frames/camera-a.raw", CancellationToken.None));
    }
}
