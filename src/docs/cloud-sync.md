# Cloud synchronization

`SmartMetrix.CloudSyncService` accepts confirmed measurements without depending on cloud availability. `POST /v1/sync/queue` copies every referenced artifact into a local durable spool and atomically persists a queue item before returning `202 Accepted`.

The stable identity of an item is derived from `measurementId` and `version`, so retrying the enqueue request does not create a duplicate. Results have priority over raw frames. A background worker uploads a manifest followed by content-range chunks; it sends full-file and per-chunk SHA-256 checksums and checkpoints the confirmed offset after every chunk. HTTP conflicts are retained for operator resolution, while transport and server errors use capped exponential backoff.

## Automatic handoff from MeasurementOrchestrator

Set `CloudDispatch__Enabled=true` and `CloudDispatch__BaseUrl` on the orchestrator.
Set `CloudSync__StorageBaseUrl` on CloudSync to the StorageService reachable from that process.
In edge Compose use `SMARTMETRIX_CLOUD_DISPATCH_ENABLED=true`; service URLs are configured there.
Dispatch is disabled by default. Configure the actual cloud receiver separately with
`CloudSync__CloudBaseUrl`; no production endpoint is assumed or provisioned.

The dispatcher scans completed measurements with a saved result URI and no `cloudQueuedAt`.
This includes older completed measurements, not just the most recent 200. Demo measurements
without a pipeline result are excluded. Simulated pipeline results retain `isTestData` in the manifest.
`POST /v1/sync/measurements` accepts measurementId, version, runId and resultUri. CloudSync reads
the result and recursively follows JSON references within that run through configured StorageService
downloads. It spools stage reports, frames, masks and clouds before acknowledging the request.
References outside the run (such as shared calibration assets) are retained in JSON but not copied.
`artifact-index.json` maps original S3 URIs to unique uploaded names. Raw frames have lower priority.

The persisted Completed record is the dispatch intent; an unavailable queue or a failed import leaves
it pending without changing measurement success. Each polling pass retries pending items and continues
past individual failures. `CloudDispatch__PollSeconds` defaults to 10; request timeout is 300 seconds.
Only after acknowledgement is `cloudQueuedAt` saved with optimistic concurrency and a new record version.
It means queued locally, not delivered to the cloud. If acknowledgement is lost, the same measurement/version
returns its durable queue record even if StorageService has since become unavailable. Upload to the cloud
continues independently with the existing checksum, retry/backoff and conflict handling.

Keep source artifacts until local queue acknowledgement and retain CloudSync data volumes. Interrupted
imports can leave `import-*` temporary directories after a process crash; reclaim them only while CloudSync
is stopped. Shared calibration binaries and production cloud acceptance remain separate deployment checks.

`CloudDispatchTests` covers recursive artifact import, lost acknowledgement, restart, durable deduplication
and invalid result references. PostgreSQL dispatch selection is covered by the infrastructure test suite.

## Queue endpoints

- `GET /v1/sync/status` returns queued items/bytes, conflicts, and the last successful synchronization time.
- `GET /v1/sync/audit?take=100` returns the durable audit trail newest-first.

## Cloud protocol

- `PUT /v1/measurements/{measurementId}/versions/{version}` registers the immutable manifest.
- `PUT /v1/measurements/{measurementId}/versions/{version}/artifacts/{name}` uploads chunks with `Content-Range`, `X-Content-SHA256`, and `X-Chunk-SHA256`.
- `POST /v1/measurements/{measurementId}/versions/{version}/complete` acknowledges the version.

Every request includes an `Idempotency-Key`. The cloud should return `409 Conflict` for a different existing version and echo `X-Chunk-SHA256` when it validates a chunk. After final acknowledgement, local spool files are deleted when `DeleteArtifactsAfterAcknowledgement` is enabled; the queue record and audit remain.
