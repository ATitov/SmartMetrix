# Object storage

`StorageService` keeps frames and large derived artifacts in a private S3-compatible bucket. Binary payloads are sent to the service over HTTP and never embedded in NATS messages.

## Object layout and policy

- Final objects: `measurements/{measurementId}/{artifactPath}`.
- Staging objects: `.uploads/{measurementId}/{uploadId}`.
- The bucket has no public policy. Consumers receive short-lived presigned download URLs.
- Final objects expire after `Storage__RetentionDays` (365 by default).
- Staging objects and incomplete multipart uploads expire after `Storage__IncompleteUploadRetentionDays` (1 by default).

The service creates the bucket and lifecycle rules during startup. Credentials must be supplied through configuration or environment variables outside local development.

## HTTP API

- `PUT /v1/measurements/{measurementId}/artifacts/{artifactPath}` uploads an object. `Content-Type` is required; optional `X-Content-SHA256` rejects a mismatched body and `X-Provenance` records the producing service/model/version.
- `GET /v1/measurements/{measurementId}/artifacts/metadata/{artifactPath}` returns the URI, SHA-256, size, MIME type, provenance, and storage time.
- `GET /v1/measurements/{measurementId}/artifacts/download/{artifactPath}` downloads the object after recomputing and validating SHA-256.
- `GET /v1/measurements/{measurementId}/artifacts/uri/{artifactPath}` returns a short-lived presigned download URL and metadata.

Uploads first spool and hash the request locally, then write a staging key and atomically copy it to the final key. Repeating the same path and bytes returns the existing metadata; different bytes for an existing path return HTTP 409.

## Integration test

The MinIO integration test follows the repository-wide opt-in convention:

```powershell
$env:SMARTMETRIX_RUN_INTEGRATION_TESTS = 'true'
dotnet test tests/SmartMetrix.ArchitectureTests/SmartMetrix.ArchitectureTests.csproj
```

Docker must be available. The test starts an isolated MinIO container, verifies upload/download/idempotency and presigning, then deliberately corrupts an object and confirms that the service detects it.
