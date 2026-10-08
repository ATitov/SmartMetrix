# Local positioning

`LocalPositioningService` resolves an excavator pose for the camera frame's hardware timestamp. `exposedAt` is the synchronized UTC instant used only to select the valid transform version; interpolation always uses `hardwareTimestampNanoseconds`. Samples from total-station/prism, IMU, encoder and optional GNSS adapters retain their hardware timestamps and source identifiers. Each source must bracket the requested timestamp; stale input is rejected with `422` and code `StalePose`.

Positions are linearly interpolated and orientations use quaternion SLERP. The resulting excavator pose is composed with the transform version valid at that instant. Every response includes `coordinateSystemId`, `transformVersion`, and the exact requested `hardwareTimestampNanoseconds`.

Covariance is a row-major 6×6 matrix ordered `[x, y, z, roll, pitch, yaw]`. Translation units are m², rotation units are rad², and off-diagonal entries are the corresponding cross-covariances. `sources` records the adapter, device, and two samples used for interpolation.

Configuration limits sample age, interpolation span, and disagreement between independent position sources. Missing, stale, or inconsistent input is never returned as a valid pose.
