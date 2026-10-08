# Three-camera capture

For unsynchronized IP-camera test capture (one camera or A/B/C), see
[RTSP camera setup](rtsp-camera.md). RTSP test responses explicitly report unknown
exposure time/skew and host receipt times; they are not hardware-synchronized captures.

`CameraService` exposes `POST /v1/measurements/{measurementId}/capture`. A successful request returns exactly one original frame from cameras A, B and C, their frame IDs and hardware timestamps, the measured timestamp skew, and immutable StorageService URIs.

Production uses `Camera__Adapter=Arena`. The native ABI is defined in `src/Services/SmartMetrix.CameraService/native/smartmetrix_arena.h`; the native implementation owns Arena SDK discovery, serial-to-A/B/C assignment, common hardware trigger configuration, and identical exposure. If the library, SDK, or rig is absent, the endpoint returns HTTP 503 with code `NotConfigured`.

## Arena native adapter

The implementation and CMake project live beside the ABI header. A build without the proprietary SDK deliberately produces a diagnostic stub that always returns `NotConfigured`; it is useful for validating packaging. Build the production library on the target ARM64 machine after installing Arena SDK:

```bash
cmake -S src/Services/SmartMetrix.CameraService/native -B build/arena \
  -DSMARTMETRIX_WITH_ARENA=ON -DARENA_SDK_ROOT=/opt/arena \
  -DCMAKE_BUILD_TYPE=Release
cmake --build build/arena --config Release
cmake --install build/arena --prefix /opt/smartmetrix/native
```

Configure the immutable channel mapping and acquisition parameters through the environment:

```text
Camera__CameraASerialNumber=<serial A>
Camera__CameraBSerialNumber=<serial B>
Camera__CameraCSerialNumber=<serial C>
Camera__TriggerSource=Line0
Camera__TriggerActivation=RisingEdge
Camera__PixelFormat=Mono8
Camera__ExposureMicroseconds=5000
Camera__CaptureTimeoutMilliseconds=10000
```

All three serials are mandatory and must be unique. Initialization configures `FrameStart` hardware triggering, disables automatic exposure, applies identical exposure/pixel format, enables packet negotiation and resend, and starts an eight-buffer stream per camera. Captured Arena buffers are copied before being requeued, so managed payloads never reference SDK-owned memory. The edge Compose profile mounts the SDK read-only at `/opt/arena`, the adapter at `/app/native`, and sets `LD_LIBRARY_PATH`; the SDK itself is not redistributed by this repository.

Hardware acceptance still requires the actual rig: verify A/B/C identity, timestamp skew, missing-frame behavior and at least 1,000 consecutive captures while monitoring process RSS.

For a Windows x64 development build, install the **Desktop development with C++** workload (MSVC and CMake), then run from a Developer PowerShell:

```powershell
cmake -S src/Services/SmartMetrix.CameraService/native -B artifacts/arena-native `
  -A x64 -DSMARTMETRIX_WITH_ARENA=ON `
  '-DARENA_SDK_ROOT=C:/Program Files/LUCID Vision Labs/Arena SDK'
cmake --build artifacts/arena-native --config Release
```

Place `smartmetrix_arena.dll` beside CameraService and add the Arena SDK `x64Release` directory to `PATH`. The deployed Jetson build must still be produced from the Linux ARM64 Arena SDK; a Windows DLL cannot be copied to Jetson.

For local deterministic tests only, set `Camera__Adapter=Simulator`. It reads the fixed `simulator-frames/camera-{a,b,c}.raw` fixtures and is reported as `Simulator` in every response; it is not a production fallback.

The managed boundary rejects duplicate/missing cameras and empty payloads as `FrameSetIncomplete`. It measures `max(timestamp)-min(timestamp)` and rejects a set as `TimestampSkewExceeded` when it exceeds `Camera__MaximumTimestampSkewNanoseconds`. Originals are uploaded to `frames/{cameraId}/{frameId}.raw` with SHA-256 verification and provenance `camera-service:original`.
