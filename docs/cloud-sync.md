# Cloud synchronization

`SmartMetrix.CloudSyncService` accepts confirmed measurements without depending on cloud availability. `POST /v1/sync/queue` copies every referenced artifact into a local durable spool and atomically persists a queue item before returning `202 Accepted`.

The stable identity of an item is derived from `measurementId` and `version`, so retrying the enqueue request does not create a duplicate. Results have priority over raw frames. A background worker uploads a manifest followed by content-range chunks; it sends full-file and per-chunk SHA-256 checksums and checkpoints the confirmed offset after every chunk. HTTP conflicts are retained for operator resolution, while transport and server errors use capped exponential backoff.

## Operator endpoints

- `GET /v1/sync/status` returns queued items/bytes, conflicts, and the last successful synchronization time.
- `GET /v1/sync/audit?take=100` returns the durable audit trail newest-first.

## Cloud protocol

- `PUT /v1/measurements/{measurementId}/versions/{version}` registers the immutable manifest.
- `PUT /v1/measurements/{measurementId}/versions/{version}/artifacts/{name}` uploads chunks with `Content-Range`, `X-Content-SHA256`, and `X-Chunk-SHA256`.
- `POST /v1/measurements/{measurementId}/versions/{version}/complete` acknowledges the version.

Every request includes an `Idempotency-Key`. The cloud should return `409 Conflict` for a different existing version and echo `X-Chunk-SHA256` when it validates a chunk. After final acknowledgement, local spool files are deleted when `DeleteArtifactsAfterAcknowledgement` is enabled; the queue record and audit remain.
