using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using SmartMetrix.StorageService;

namespace SmartMetrix.ArchitectureTests;

public sealed class StorageRequirementsTests
{
    [Theory]
    [Trait("Requirement", "STO-03")]
    [InlineData("")]
    [InlineData("../secret")]
    [InlineData("frames/../secret")]
    [InlineData("frames//a.raw")]
    [InlineData("frames/./a.raw")]
    [InlineData("frames\\..\\a.raw")]
    public async Task InvalidPathsFailBeforeContactingStorage(string path)
    {
        using var s3 = new NoNetworkS3();
        var storage = new ObjectStorage(s3, Options.Create(new StorageOptions()));
        await Assert.ThrowsAsync<ArgumentException>(() => storage.GetMetadataAsync(Guid.NewGuid(), path, CancellationToken.None));
        Assert.Equal(0, s3.Requests);
    }

    [Fact]
    [Trait("Requirement", "STO-02")]
    public async Task InvalidChecksumMetadataIsNeverReturnedAsVerifiedArtifact()
    {
        using var s3 = new NoNetworkS3();
        var storage = new ObjectStorage(s3, Options.Create(new StorageOptions()));
        await Assert.ThrowsAsync<ArtifactIntegrityException>(() =>
            storage.GetMetadataAsync(Guid.NewGuid(), "frames/a.raw", CancellationToken.None));
        Assert.Equal(1, s3.Requests);
    }

    private sealed class NoNetworkS3() : AmazonS3Client(new AnonymousAWSCredentials(),
        new AmazonS3Config { ServiceURL = "http://unused", ForcePathStyle = true })
    {
        public int Requests { get; private set; }
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(string bucketName, string key, CancellationToken cancellationToken = default)
        {
            Requests++;
            var response = new GetObjectMetadataResponse();
            response.Metadata["sha256"] = "corrupt";
            return Task.FromResult(response);
        }
    }
}
