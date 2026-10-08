# Georeference service

`POST /v1/measurements/{measurementId}/georeference` converts camera-frame point clouds and block geometry into quarry-local XYZ.

The request supplies an ordered, versioned `camera -> rig -> platform -> excavator` chain and an exposure-time `excavator -> quarry` pose. The service rejects timestamp mismatches, gaps in the coordinate-system chain, missing versions, invalid rotations, and malformed 6x6 covariance matrices.

The response includes the quarry `coordinateSystemId`, the exposure hardware timestamp, localized points and block centres/boundaries, a positional uncertainty per object, and provenance for every transform version used. Uncertainty combines positional variance with angular variance scaled by distance from the camera origin.
