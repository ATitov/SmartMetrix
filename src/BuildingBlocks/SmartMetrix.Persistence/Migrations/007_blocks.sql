DO $migration$
BEGIN
IF '__schema__' = 'measurement' THEN
    ALTER VIEW measurement.blocks RENAME TO block_projection_source;
    CREATE TABLE measurement.block_records AS SELECT * FROM measurement.block_projection_source;
    ALTER TABLE measurement.block_records ADD PRIMARY KEY(run_id,block_id);
    ALTER TABLE measurement.block_records ADD FOREIGN KEY(measurement_id) REFERENCES measurement.measurements(id);
    CREATE INDEX ON measurement.block_records(measurement_id);
    CREATE INDEX ON measurement.block_records(coordinate_system_id,x_metres,y_metres);
    CREATE VIEW measurement.blocks AS SELECT * FROM measurement.block_records;
    EXECUTE $function$
        CREATE FUNCTION measurement.project_result_blocks() RETURNS trigger LANGUAGE plpgsql AS $body$
        BEGIN
            IF NEW.stage = 'result' THEN
                INSERT INTO measurement.block_records SELECT * FROM measurement.block_projection_source WHERE run_id=NEW.run_id;
            END IF;
            RETURN NEW;
        END $body$
    $function$;
    CREATE TRIGGER project_result_blocks AFTER INSERT ON measurement.stages
        FOR EACH ROW EXECUTE FUNCTION measurement.project_result_blocks();
END IF;
END $migration$;
