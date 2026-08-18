namespace SmartMetrix.StorageService;

public sealed record ArtifactMetadata(
    Guid MeasurementId,
    string ArtifactPath,
    string Uri,
    string Sha256,
    long Size,
    string ContentType,
    string? Provenance,
    DateTimeOffset StoredAt);

public sealed record UploadResult(ArtifactMetadata Metadata, bool Created);

public sealed record PresignedArtifact(ArtifactMetadata Metadata, string DownloadUri, DateTimeOffset ExpiresAt);

public sealed class ArtifactDownload(Stream content, ArtifactMetadata metadata) : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public ArtifactMetadata Metadata { get; } = metadata;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed class ArtifactIntegrityException(string message) : IOException(message);
public sealed class ArtifactConflictException(string message) : InvalidOperationException(message);
