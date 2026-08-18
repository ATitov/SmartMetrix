using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace SmartMetrix.StorageService;

public sealed class ObjectStorage(IAmazonS3 s3, IOptions<StorageOptions> options)
{
    private const string ShaMetadata = "sha256";
    private const string MeasurementMetadata = "measurement-id";
    private const string PathMetadata = "artifact-path";
    private const string ProvenanceMetadata = "provenance";
    private const string StoredAtMetadata = "stored-at";
    private readonly StorageOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal);

    public async Task<UploadResult> UploadAsync(
        Guid measurementId,
        string artifactPath,
        Stream content,
        string contentType,
        string? expectedSha256,
        string? provenance,
        CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeArtifactPath(artifactPath);
        var key = BuildKey(measurementId, normalizedPath);
        var gate = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        var tempFile = Path.Combine(Path.GetTempPath(), $"smartmetrix-{Guid.NewGuid():N}.upload");
        var tempKey = $".uploads/{measurementId:D}/{Guid.NewGuid():N}";

        try
        {
            var (sha256, size) = await SpoolAndHashAsync(content, tempFile, cancellationToken);
            if (expectedSha256 is not null &&
                (!IsSha256(expectedSha256) || !sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArtifactIntegrityException($"Expected SHA-256 {expectedSha256}, received {sha256}.");
            }

            var existing = await TryGetMetadataAsync(measurementId, normalizedPath, cancellationToken);
            if (existing is not null)
            {
                if (existing.Sha256 == sha256 && existing.Size == size)
                {
                    return new UploadResult(existing, false);
                }

                throw new ArtifactConflictException($"Artifact '{normalizedPath}' already exists with different content.");
            }

            var storedAt = DateTimeOffset.UtcNow;
            var put = new PutObjectRequest
            {
                BucketName = _options.Bucket,
                Key = tempKey,
                FilePath = tempFile,
                ContentType = contentType
            };
            AddMetadata(put.Metadata, measurementId, normalizedPath, sha256, provenance, storedAt);
            await s3.PutObjectAsync(put, cancellationToken);

            await s3.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _options.Bucket,
                SourceKey = tempKey,
                DestinationBucket = _options.Bucket,
                DestinationKey = key,
                MetadataDirective = S3MetadataDirective.COPY
            }, cancellationToken);

            var metadata = await GetMetadataAsync(measurementId, normalizedPath, cancellationToken);
            if (metadata.Sha256 != sha256 || metadata.Size != size)
            {
                throw new ArtifactIntegrityException("Stored object metadata does not match the uploaded content.");
            }

            return new UploadResult(metadata, true);
        }
        finally
        {
            try { await s3.DeleteObjectAsync(_options.Bucket, tempKey, CancellationToken.None); }
            catch (Exception) { /* lifecycle policy is the final cleanup safety net */ }
            try { if (File.Exists(tempFile)) File.Delete(tempFile); }
            catch (IOException) { /* the OS can reclaim a stranded temporary file */ }
            gate.Release();
            _keyLocks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(key, gate));
        }
    }

    public async Task<ArtifactMetadata> GetMetadataAsync(Guid measurementId, string artifactPath, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeArtifactPath(artifactPath);
        var response = await s3.GetObjectMetadataAsync(_options.Bucket, BuildKey(measurementId, normalizedPath), cancellationToken);
        return MapMetadata(measurementId, normalizedPath, response.Headers.ContentType, response.ContentLength, response.Metadata);
    }

    public async Task<ArtifactDownload> DownloadVerifiedAsync(Guid measurementId, string artifactPath, CancellationToken cancellationToken)
    {
        var normalizedPath = NormalizeArtifactPath(artifactPath);
        using var response = await s3.GetObjectAsync(_options.Bucket, BuildKey(measurementId, normalizedPath), cancellationToken);
        var metadata = MapMetadata(measurementId, normalizedPath, response.Headers.ContentType, response.ContentLength, response.Metadata);
        var verifiedPath = Path.Combine(Path.GetTempPath(), $"smartmetrix-{Guid.NewGuid():N}.download");
        var verified = new FileStream(
            verifiedPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await response.ResponseStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await verified.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (actual != metadata.Sha256 || verified.Length != metadata.Size)
        {
            await verified.DisposeAsync();
            throw new ArtifactIntegrityException($"Artifact '{normalizedPath}' is corrupted.");
        }

        verified.Position = 0;
        return new ArtifactDownload(verified, metadata);
    }

    public async Task<PresignedArtifact> GetPresignedDownloadAsync(Guid measurementId, string artifactPath, CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(measurementId, artifactPath, cancellationToken);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(_options.PresignedUrlLifetimeSeconds);
        var uri = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = BuildKey(measurementId, metadata.ArtifactPath),
            Verb = HttpVerb.GET,
            Expires = expiresAt.UtcDateTime,
            Protocol = Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps
                ? Protocol.HTTPS
                : Protocol.HTTP
        });
        return new PresignedArtifact(metadata, uri, expiresAt);
    }

    internal static string BuildKey(Guid measurementId, string artifactPath) =>
        $"measurements/{measurementId:D}/{NormalizeArtifactPath(artifactPath)}";

    internal static string NormalizeArtifactPath(string artifactPath)
    {
        var normalized = artifactPath.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Artifact path must be a non-empty relative path without traversal segments.", nameof(artifactPath));
        }
        return normalized;
    }

    private async Task<ArtifactMetadata?> TryGetMetadataAsync(Guid measurementId, string path, CancellationToken cancellationToken)
    {
        try { return await GetMetadataAsync(measurementId, path, cancellationToken); }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    private static async Task<(string Sha256, long Size)> SpoolAndHashAsync(Stream content, string path, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            size += read;
        }
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), size);
    }

    private static void AddMetadata(MetadataCollection metadata, Guid measurementId, string path, string sha, string? provenance, DateTimeOffset storedAt)
    {
        metadata[ShaMetadata] = sha;
        metadata[MeasurementMetadata] = measurementId.ToString("D");
        metadata[PathMetadata] = path;
        metadata[StoredAtMetadata] = storedAt.ToString("O", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(provenance)) metadata[ProvenanceMetadata] = Convert.ToBase64String(Encoding.UTF8.GetBytes(provenance));
    }

    private ArtifactMetadata MapMetadata(Guid measurementId, string path, string contentType, long size, MetadataCollection metadata)
    {
        var sha = metadata[ShaMetadata];
        if (!IsSha256(sha)) throw new ArtifactIntegrityException($"Artifact '{path}' has invalid SHA-256 metadata.");
        if (!Guid.TryParse(metadata[MeasurementMetadata], out var storedMeasurementId) || storedMeasurementId != measurementId ||
            !string.Equals(metadata[PathMetadata], path, StringComparison.Ordinal))
        {
            throw new ArtifactIntegrityException($"Artifact '{path}' has inconsistent identity metadata.");
        }
        if (!DateTimeOffset.TryParse(metadata[StoredAtMetadata], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var storedAt))
        {
            throw new ArtifactIntegrityException($"Artifact '{path}' has invalid storage timestamp metadata.");
        }
        var encodedProvenance = metadata[ProvenanceMetadata];
        var provenance = string.IsNullOrWhiteSpace(encodedProvenance)
            ? null
            : Encoding.UTF8.GetString(Convert.FromBase64String(encodedProvenance));
        return new ArtifactMetadata(measurementId, path, $"s3://{_options.Bucket}/{BuildKey(measurementId, path)}", sha, size, contentType, provenance, storedAt);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => char.IsAsciiHexDigit(character));
}
