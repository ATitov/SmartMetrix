# Segmentation service

`SegmentationService` accepts RGB8 frames and emits a three-class mask: `Background=0`, `Rock=1`, `Crack=2`.
Images, including the production 2448×2048 format, are split into overlapping model-sized tiles. Weighted overlap blending prevents tile seams.

Configuration selects `OnnxRuntime` for development or `TensorRT` with `FP16`/`INT8` precision. The singleton model session is loaded and warmed once during application startup. The model version, average confidence, low-confidence flag, mask URI, and per-pixel confidence-map URI are returned with every result. Model dimensions, RGB8 input format, provider, precision, and version are validated before inference.

The deterministic golden fixture in `SegmentationGoldenTests` fixes preprocessing, class mapping, mask bytes, confidence behavior, tiling boundaries, and one-time model initialization.
