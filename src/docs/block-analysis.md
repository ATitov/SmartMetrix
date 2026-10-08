# Block analysis

`BlockAnalysisService` accepts an organized point cloud and aligned segmentation/confidence maps at
`POST /v1/measurements/{measurementId}/block-analysis`. Point coordinates are metres. Output axes and D-percentiles
are millimetres, projected area is square millimetres, and ellipsoid volume is cubic millimetres.

Rock pixels are grouped with 4-neighbour connected components. Crack/background pixels and configurable 3D depth
discontinuities split touching objects; radial outliers are removed before geometry is estimated. D10/D50/D80/D95
are cumulative passing percentiles weighted by estimated block volume. Every block retains the measurement ID,
coordinate system, calibration ID and all source artifact URIs. Border contact marks partial visibility. Confidence is
the geometric mean of depth, segmentation and calibration confidence, with stable reason codes and algorithm provenance.
