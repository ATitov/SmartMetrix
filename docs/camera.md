# Three-camera capture

`CameraService` exposes `POST /v1/measurements/{measurementId}/capture`. A successful request returns exactly one original frame from cameras A, B and C, their frame IDs and hardware timestamps, the measured timestamp skew, and immutable StorageService URIs.

Production uses `Camera__Adapter=Arena`. The native ABI is defined in `src/Services/SmartMetrix.CameraService/native/smartmetrix_arena.h`; the native implementation owns Arena SDK discovery, serial-to-A/B/C assignment, common hardware trigger configuration, and identical exposure. If the library, SDK, or rig is absent, the endpoint returns HTTP 503 with code `NotConfigured`.

For local deterministic tests only, set `Camera__Adapter=Simulator`. It reads the fixed `simulator-frames/camera-{a,b,c}.raw` fixtures and is reported as `Simulator` in every response; it is not a production fallback.

The managed boundary rejects duplicate/missing cameras and empty payloads as `FrameSetIncomplete`. It measures `max(timestamp)-min(timestamp)` and rejects a set as `TimestampSkewExceeded` when it exceeds `Camera__MaximumTimestampSkewNanoseconds`. Originals are uploaded to `frames/{cameraId}/{frameId}.raw` with SHA-256 verification and provenance `camera-service:original`.
