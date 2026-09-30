DO $migration$
BEGIN
IF '__schema__' = 'measurement' THEN
    CREATE TABLE measurement.measurements (
        id uuid PRIMARY KEY,
        excavator_id text NOT NULL,
        coordinate_system_id text NOT NULL,
        status text NOT NULL,
        version bigint NOT NULL CHECK (version >= 0),
        requested_at timestamptz NOT NULL,
        updated_at timestamptz NOT NULL,
        is_test_data boolean NOT NULL,
        payload jsonb NOT NULL
    );
    CREATE INDEX ON measurement.measurements(updated_at DESC, id);
    CREATE INDEX ON measurement.measurements(excavator_id, requested_at DESC);
    CREATE INDEX ON measurement.measurements(updated_at DESC) WHERE status NOT IN ('Completed', 'Rejected', 'Failed');
    CREATE TABLE measurement.history (
        measurement_id uuid NOT NULL REFERENCES measurement.measurements(id),
        version bigint NOT NULL,
        recorded_at timestamptz NOT NULL DEFAULT now(),
        payload jsonb NOT NULL,
        PRIMARY KEY(measurement_id, version)
    );
    CREATE TABLE measurement.stages (
        run_id uuid NOT NULL,
        stage text NOT NULL,
        measurement_id uuid NOT NULL REFERENCES measurement.measurements(id),
        artifact_uri text NOT NULL,
        payload jsonb NOT NULL,
        created_at timestamptz NOT NULL DEFAULT now(),
        PRIMARY KEY(run_id, stage)
    );
    CREATE INDEX ON measurement.stages(measurement_id, run_id);
END IF;
END $migration$;
