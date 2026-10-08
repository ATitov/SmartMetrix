# Native GPU stereo backend

`src/Services/SmartMetrix.DepthService/native` contains ABI v1 and the `libsmartmetrix_stereo` implementation. It uploads rectified `Mono8` pairs to CUDA, runs OpenCV `StereoSGM` in both directions, downloads fixed-point disparity, applies speckle filtering and left-right consistency, and returns one float disparity plus normalized confidence per pixel. Invalid pixels use `NaN` disparity and zero confidence.

Input images must already be rectified with the maps identified by `RectificationMapUri`; the native matcher does not fetch remote calibration artifacts.

Build on Jetson against OpenCV with the `cudastereo` module:

```bash
cmake -S src/Services/SmartMetrix.DepthService/native -B build/stereo \
  -DSMARTMETRIX_WITH_OPENCV_CUDA=ON -DCMAKE_BUILD_TYPE=Release
cmake --build build/stereo --config Release
cmake --install build/stereo --prefix /opt/smartmetrix/native
```

Runtime configuration:

```text
Depth__Backend=Native
Depth__NativeProvider=OpenCvCuda
Depth__MinimumDisparity=1
Depth__MaximumDisparity=96
Depth__LeftRightTolerancePixels=1.5
Depth__MinimumConfidence=0.15
Depth__UniquenessRatio=10
```

VPI is mounted in the edge container for a future provider, but the implemented provider is currently `OpenCvCuda`. Selecting another provider returns `NotConfigured`; the service never silently falls back to CPU.
