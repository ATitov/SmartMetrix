# E2E simulator and MVP field acceptance

The versioned golden dataset drives a deterministic camera and positioning simulator through
`capture -> quality -> depth -> segmentation -> analysis -> georeference`. Run it from the repository root:

```powershell
dotnet run --project tools/SmartMetrix.E2ESimulator
```

The command exits with `0` only when every acceptance check passes and writes `artifacts/e2e/report.json`,
the simulated point cloud, segmentation mask, confidence map, per-stage provenance and diagnostic metrics.
Dataset, model, calibration and transform versions are recorded in the report. A changed dataset must use a
new version and explicitly define its reference D50/D80, expected position and thresholds.

## Field protocol

Repeat the protocol at 12, 20 and 30 metres in daylight, shadow, dust and after lens cleaning. For each
distance/condition pair capture at least 30 synchronized frame sets, record a surveyed control point, manually
measure/label the visible blocks and keep the raw frames, calibration, pose and model version immutable.

The pilot passes only when the aggregate report confirms all configured checks: relative D50 and D80 errors,
coordinate error, wall-clock duration and rejected-frame fraction. The default golden thresholds are 15% for
D50/D80, 5 cm for position, 5 seconds per simulated measurement and 0% rejected golden frames. Field limits may
be tightened in a new dataset version, but must never be relaxed after data is collected.
