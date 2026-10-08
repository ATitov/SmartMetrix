# ControlPointService

The service stores versioned quarry control points in metres, including coordinate system, accuracy in millimetres, measurement date, status, and either a coded visual mark or prism target. Every change appends a version and audit entry. Inactive points are excluded from `activeOnly` queries and cannot be registered in a new measurement solution. A point referenced by a measurement cannot be deleted.

The HTTP API exposes CRUD, version/audit history, survey observations, measurement usage, and CSV/GeoJSON import/export under `/control-points`. Send `X-Actor` to identify the operator in the audit trail. CSV coordinates explicitly declare `Metres` or `Millimetres`; GeoJSON coordinates are metres. Import responses contain totals plus an error entry with row/feature number, point ID, field, and message for every rejected item.
