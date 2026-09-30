# Segmentation service

`SegmentationService` accepts RGB8 frames and emits a three-class mask: `Background=0`, `Rock=1`, `Crack=2`.
The default `Deterministic` backend is a test implementation. It splits images into overlapping tiles and blends their outputs; its masks are not learned predictions.

For real inference select `StoneVision`, the HTTP adapter to YOLO + MobileSAM. It preserves instance masks and IDs for BlockAnalysis. StoneVision currently emits only background and rock; crack detection is unsupported. Readiness requires loaded upstream models, and dependency failures never fall back to synthetic masks. See [configuration and limitations](stonevision.md) and [API contract](segmentation-api.md).

The deterministic backend validates `OnnxRuntime`/`TensorRT` provider settings but does not execute either native inference runtime. Those integrations remain future work. Results include model version, confidence, warnings and artifact URIs.

The deterministic golden fixture in `SegmentationGoldenTests` fixes preprocessing, class mapping, mask bytes, confidence behavior, tiling boundaries, and one-time model initialization.
