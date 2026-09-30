DO $migration$
BEGIN
IF '__schema__' = 'measurement' THEN
    CREATE VIEW measurement.results AS
    SELECT s.measurement_id, s.run_id, s.created_at,
        (s.payload->>'isTestData')::boolean AS is_test_data,
        s.payload->>'coordinateSystemId' AS coordinate_system_id,
        s.payload->>'calibrationId' AS calibration_id,
        s.payload->>'calibrationVersion' AS calibration_version,
        s.payload->>'modelVersion' AS model_version,
        s.payload->>'transformVersion' AS transform_version,
        (s.payload#>>'{analysis,d10Millimetres}')::double precision AS d10_mm,
        (s.payload#>>'{analysis,d50Millimetres}')::double precision AS d50_mm,
        (s.payload#>>'{analysis,d80Millimetres}')::double precision AS d80_mm,
        (s.payload#>>'{analysis,d95Millimetres}')::double precision AS d95_mm,
        (s.payload#>>'{analysis,oversizeCount}')::integer AS oversize_count,
        (s.payload#>>'{analysis,confidence}')::double precision AS confidence,
        s.payload#>'{analysis,sizeClasses}' AS size_classes,
        s.payload#>'{analysis,provenance}' AS provenance,
        s.artifact_uri
    FROM measurement.stages s WHERE s.stage = 'result';

    CREATE VIEW measurement.blocks AS
    SELECT s.measurement_id, s.run_id, (block->>'blockId')::uuid AS block_id,
        (block->>'isValid')::boolean AS is_valid,
        (block->>'isPartiallyVisible')::boolean AS is_partially_visible,
        (block->>'confidence')::double precision AS confidence,
        (block#>>'{geometry,equivalentDiameterMillimetres}')::double precision AS diameter_mm,
        (block#>>'{geometry,majorAxisMillimetres}')::double precision AS major_axis_mm,
        (block#>>'{geometry,intermediateAxisMillimetres}')::double precision AS intermediate_axis_mm,
        (block#>>'{geometry,minorAxisMillimetres}')::double precision AS minor_axis_mm,
        (block#>>'{geometry,projectedAreaSquareMillimetres}')::double precision AS area_mm2,
        (block#>>'{geometry,volumeCubicMillimetres}')::double precision AS volume_mm3,
        s.payload->>'coordinateSystemId' AS coordinate_system_id,
        (localized#>>'{centre,xMetres}')::double precision AS x_metres,
        (localized#>>'{centre,yMetres}')::double precision AS y_metres,
        (localized#>>'{centre,zMetres}')::double precision AS z_metres,
        (localized->>'positionUncertaintyMetres')::double precision AS position_uncertainty_metres,
        localized->'boundary' AS boundary,
        block->'qualityReasons' AS quality_reasons,
        block->'sourceArtifacts' AS source_artifacts
    FROM measurement.stages s
    CROSS JOIN LATERAL jsonb_array_elements(s.payload#>'{analysis,blocks}') block
    LEFT JOIN LATERAL jsonb_array_elements(s.payload#>'{georeference,blocks}') localized
        ON localized->>'blockId' = block->>'blockId'
    WHERE s.stage = 'result';

    CREATE VIEW measurement.transitions AS
    SELECT m.id AS measurement_id, (t->>'version')::bigint AS version,
        t->>'from' AS from_status, t->>'to' AS to_status,
        (t->>'occurredAt')::timestamptz AS occurred_at,
        (t->>'commandId')::uuid AS command_id, t->>'reason' AS reason
    FROM measurement.measurements m CROSS JOIN LATERAL jsonb_array_elements(m.payload->'transitions') t;
END IF;
IF '__schema__' = 'storage' THEN
    CREATE VIEW storage.artifacts AS
    SELECT (payload->>'measurementId')::uuid AS owner_id,
        payload->>'artifactPath' AS artifact_path, payload->>'uri' AS object_uri,
        payload->>'sha256' AS sha256, (payload->>'size')::bigint AS size_bytes,
        payload->>'contentType' AS content_type, payload->>'provenance' AS provenance,
        (payload->>'storedAt')::timestamptz AS stored_at
    FROM storage.documents WHERE key LIKE 'artifact:%';
END IF;
IF '__schema__' = 'operator' THEN
    CREATE VIEW operator.reviews AS
    SELECT (r->>'measurementId')::uuid AS measurement_id, r->>'scopeId' AS scope_id,
        (r->>'commandId')::uuid AS command_id, (r->>'resultVersion')::bigint AS result_version,
        r->>'actor' AS actor, r->>'decision' AS decision, r->>'reason' AS reason, r AS payload
    FROM operator.documents d CROSS JOIN LATERAL jsonb_array_elements(d.payload) r WHERE d.key='reviews';
END IF;
END $migration$;
